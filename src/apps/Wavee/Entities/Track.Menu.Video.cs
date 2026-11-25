// ── Entities/Track.Menu.Video.cs ───────────────────────────────────────────────────────────────────────────────────
// The named partial of Track.Menu.cs: the five Video ▸ VERBS (AttachVideo · ReplaceVideo · LocateVideo ·
// ShowVideoInExplorer · RemoveVideo) registered into AppActions.
//
// Role: UI
// Owner: M
// Wave: 5 (fix — the submenu shipped in Track.Menu.cs with no verbs behind it, so `Actions.Menu.Row` returned null for
//          every row and `VideoItem` dropped the whole submenu)
// Budget: 200 lines
// Spec: ch 01 §menus row 8 (the Video ▸ submenu); ch 19 items 28–34 (its seven toasts)
//
// ── WHAT THEY DO ─────────────────────────────────────────────────────────────────────────────────────────────────────
//
// A track's video of mine is a video file LINKED to it as one of its versions ("Your files and Spotify", one concept: Shell/Video.LinkedVideos.cs).
// Attach and Replace add the picked file to the library and link it (`Video.LinkedVideos.Attach`: LocalLibrary.AddFile + LocalLink.LinkVersion);
// Remove unlinks it (the file stays in the library). Every DECISION is `Video.LinkedVideoUx`'s (pure); these verbs do only what the links
// cannot: run the modal picker, raise the toast (with its Undo) and reveal a file in Explorer.
//
// WHICH ROWS EXIST is NOT re-decided here. `VideoItem` (Track.Menu.cs) asks `Video.LinkedVideoUx.MenuFor` and each verb's
// `IsEnabled` asks the SAME function through <see cref="Track.OffersVideoVerb"/>, so a row that is present is always a
// row that runs. "Linked" vs "replaced" is read off what `Attach` returns.

using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Localization;

namespace Wavee;

public readonly partial struct Track
{
    /// <summary>The category these human-rate UI events log on (play-time ones ride "playback").</summary>
    const string VideoLogCategory = "ui";

    // ══ 1. THE SHARED GATE ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Does the Video ▸ submenu offer <paramref name="verb"/> for this playable right now? The ONE gate all
    /// five verbs' <c>IsEnabled</c> predicates share, and it is `Video.LinkedVideoUx.MenuFor` — the same flags
    /// <c>VideoItem</c> builds its rows from, walking the same tier decision playback takes. A row and its verb can
    /// therefore never disagree.
    /// <para>Multi-select has no meaning here (one file, one playable), which is why <see cref="ActionTarget.Single"/>
    /// is both the gate and the accessor: for a selection the whole submenu is absent.</para></summary>
    public static bool OffersVideoVerb(Video.MenuItems verb, string? playableUri)
    {
        // A local file's row is its own file: it takes no linked video (its video plays as itself), so no Video ▸ verb is offered there.
        if (playableUri is not { Length: > 0 } || !Video.LinkedVideos.CanLink(playableUri)) return false;
        var which = Video.LinkedVideoUx.MenuFor(true, playableUri, Video.LinkedVideos.Present,
                                             Video.LinkedVideos.Has(playableUri), Video.LinkedVideos.Decide(playableUri).Tier);
        return (which & verb) != 0;
    }

    /// <summary>The single playable this submenu acts on, or null (no single target / no uri).</summary>
    static string? VideoUriOf(in ActionTarget target)
        => target.Single is { } t && t.Uri.IsValid ? t.Uri.Text : null;

    // ══ 2. THE FIVE VERBS ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Register the five Video ▸ verbs into <see cref="AppActions"/> — AttachVideo · ReplaceVideo ·
    /// LocateVideo · ShowVideoInExplorer · RemoveVideo. Called from <see cref="InstallActions"/> (idempotent, and
    /// <c>AppActions.Register</c> is first-wins per id). Attach and Replace are the SAME mutation over the uri primary
    /// key but two identities, because the label, the icon and the undo semantics differ.</summary>
    static void InstallVideoActions()
    {
        AppActions.Register(new AppAction
        {
            Id = ActionId.AttachVideo, IconKey = ActionIcons.Video,
            Label = static _ => Loc.Get(Strings.LinkedVideo.Attach),
            IsEnabled = static c => OffersVideoVerb(Video.MenuItems.Attach, VideoUriOf(c.Target)),
            Execute = static c => PickAndAttach(VideoUriOf(c.Target), Loc.Get(Strings.LinkedVideo.PickTitle), locate: false),
        });

        AppActions.Register(new AppAction
        {
            Id = ActionId.ReplaceVideo, IconKey = ActionIcons.Replace,
            Label = static _ => Loc.Get(Strings.LinkedVideo.Replace),
            IsEnabled = static c => OffersVideoVerb(Video.MenuItems.Replace, VideoUriOf(c.Target)),
            Execute = static c => PickAndAttach(VideoUriOf(c.Target), Loc.Get(Strings.LinkedVideo.PickTitle), locate: false),
        });

        // Repair a moved file. The same mutation as Replace; only the dialog caption and the offered state differ — the
        // picker cannot be told to open AT a folder, so the previous path's deepest surviving ancestor is surfaced in
        // the caption instead of being silently lost (`LinkedVideoUx.NearestExistingAncestor`).
        AppActions.Register(new AppAction
        {
            Id = ActionId.LocateVideo, IconKey = ActionIcons.Locate,
            Label = static _ => Loc.Get(Strings.LinkedVideo.Locate),
            IsEnabled = static c => OffersVideoVerb(Video.MenuItems.Locate, VideoUriOf(c.Target)),
            Execute = static c => PickAndAttach(VideoUriOf(c.Target), Loc.Get(Strings.LinkedVideo.LocateTitle), locate: true),
        });

        AppActions.Register(new AppAction
        {
            Id = ActionId.ShowVideoInExplorer, IconKey = ActionIcons.RevealFolder,
            Label = static _ => Loc.Get(Strings.LinkedVideo.ShowInExplorer),
            IsEnabled = static c => OffersVideoVerb(Video.MenuItems.ShowInExplorer, VideoUriOf(c.Target)),
            Execute = static c =>
            {
                if (VideoUriOf(c.Target) is not { Length: > 0 } uri) return;
                if (Video.LinkedVideos.TryGet(uri, out var o)) RevealInExplorer(o.Path);
            },
        });

        // Detach — applied IMMEDIATELY with no confirmation dialog: it is metadata-only, it never touches the file on
        // disk, and it is undoable, which is exactly the case where undo beats confirm.
        AppActions.Register(new AppAction
        {
            Id = ActionId.RemoveVideo, IconKey = ActionIcons.Remove, Destructive = true,
            Label = static _ => Loc.Get(Strings.LinkedVideo.Remove),
            IsEnabled = static c => OffersVideoVerb(Video.MenuItems.Remove, VideoUriOf(c.Target)),
            Execute = static c =>
            {
                if (VideoUriOf(c.Target) is not { Length: > 0 } uri) return;
                // Snapshot BEFORE the mutation — the record is what an Undo restores.
                if (!Video.LinkedVideos.TryGet(uri, out var previous) || !Video.LinkedVideos.Remove(uri)) return;
                Log.Event(WaveeLogLevel.Info, VideoLogCategory, "linkedvideo.menu.remove", "detached the attached video",
                    fields: [WaveeLogField.Of("uri", uri), WaveeLogField.Of("path", previous.Path)]);
                string path = previous.Path, key = previous.SourceKey;
                Notify.Say(Loc.Get(Strings.LinkedVideo.Removed), InfoBarSeverity.Success,
                    Loc.Get(Strings.LinkedVideo.Undo), () => RestoreVideo(uri, path, key));
            },
        });
    }

    // ══ 3. THE SHARED PICK → VALIDATE → ATTACH → TOAST PATH ══════════════════════════════════════════════════════════

    /// <summary>Pick a file, validate it, attach it, toast it (with Undo). The ONE path Attach, Replace and the
    /// "Locate…" repair share, so those three can never drift apart. The dialog is <see cref="Pickers.Pick"/>'s: modal
    /// to the window but on its own thread, so the UI thread never blocks on it (#155); the rest runs when it answers.</summary>
    static void PickAndAttach(string? playableUri, string title, bool locate)
    {
        if (playableUri is not { Length: > 0 } uri) return;
        bool had = Video.LinkedVideos.TryGet(uri, out var previous);
        string? start = locate && had
            ? Video.LinkedVideoUx.NearestExistingAncestor(previous.Path, Video.LinkedVideos.DirectoryExists)
            : null;

        Pickers.Pick(
            PickerRequest.Open(start is null ? title : title + " — " + start,
                               Video.LinkedVideoUx.PickerFilter(Loc.Get(Strings.LinkedVideo.Filter))),
            done: picked => { if (picked is not null) Attached(picked); },                      // null = cancelled: nothing happened
            failed: ex => Notify.Say(ex.Message, InfoBarSeverity.Error));                       // ch 19 item 29

        // The dialog answers later, on the UI thread (Pickers: its own thread, modal to the window).
        void Attached(string picked)
        {
            var rejection = Video.LinkedVideoUx.Validate(picked, Video.LinkedVideos.FileExists);
            if (rejection != Video.AttachRejection.None)
            {
                bool notMp4 = rejection == Video.AttachRejection.NotMp4;
                Notify.Say(Loc.Get(notMp4 ? Strings.LinkedVideo.RejectedNotMp4 : Strings.LinkedVideo.RejectedNotFound),
                           InfoBarSeverity.Error);                                                 // ch 19 item 30
                Log.Event(WaveeLogLevel.Warning, VideoLogCategory, "linkedvideo.attach.rejected", "the picked file was refused",
                    fields: [WaveeLogField.Of("path", picked), WaveeLogField.Of("reason", notMp4 ? "not-mp4" : "not-found")]);
                return;
            }

            if (Video.LinkedVideos.Attach(uri, picked, SourceKeyFor(picked), NowUnix, out var refused) is not { } kind)
            {
                // The linked videos refused what validation had just accepted: the file went away between the two probes ("couldn't be
                // found"), or the library or the link store did not take it. The toast says which (ch 19 item 31).
                Notify.Say(Video.LinkedVideos.RefusalText(refused), InfoBarSeverity.Error);
                Log.Event(WaveeLogLevel.Warning, VideoLogCategory, "linkedvideo.attach.rejected", "the picked file was refused",
                    fields: [WaveeLogField.Of("path", picked), WaveeLogField.Of("reason", refused.ToString())]);
                return;
            }

            bool replaced = kind == Video.LinkedVideoChange.Replace;
            Log.Event(WaveeLogLevel.Info, VideoLogCategory, replaced ? "linkedvideo.menu.replace" : "linkedvideo.menu.attach",
                replaced ? "replaced the attached video" : "attached a local video",
                fields: [WaveeLogField.Of("uri", uri), WaveeLogField.Of("path", picked)]);

            // Undo restores the PREVIOUS state exactly: the prior linked video on a replace, no linked video on a first attach.
            string previousPath = had ? previous.Path : "", previousKey = had ? previous.SourceKey : "";
            Notify.Say(Loc.Get(replaced ? Strings.LinkedVideo.Replaced : Strings.LinkedVideo.Attached),
                       InfoBarSeverity.Success, Loc.Get(Strings.LinkedVideo.Undo),
                       () => RestoreVideo(uri, previousPath, previousKey));                        // ch 19 item 32
            // The REVEAL is the host's (`Video`'s `Changed` handler, after the has-video commit); showing the surface here
            // would race that posted mutation.
        }
    }

    /// <summary>Undo: re-attach the previous file, or detach when there was none. Both directions go through the same
    /// linked-video list mutations, so the host's latch/cache/reload flow fires for the undo exactly as it did for the change.</summary>
    static void RestoreVideo(string uri, string previousPath, string previousSourceKey)
    {
        if (previousPath.Length == 0)
        {
            // Undoing a FIRST attach: detach. Remove is idempotent, so an linked video the user has meanwhile removed
            // themselves still leaves the undo's goal — nothing attached — true.
            Video.LinkedVideos.Remove(uri);
        }
        else if (Video.LinkedVideos.Attach(uri, previousPath, previousSourceKey, NowUnix, out var refused) is null)
        {
            Notify.Say(Video.LinkedVideos.RefusalText(refused), InfoBarSeverity.Error);  // ch 19 item 33
            return;
        }
        Notify.Say(Loc.Get(Strings.LinkedVideo.Restored), InfoBarSeverity.Informational);      // ch 19 item 34
    }

    /// <summary>The recorded source identity of a local linked video: the PATH. It is one half of the quarantine pair —
    /// the play path writes <c>Video.LinkedVideos.Quarantined(uri, recordedKey)</c> and <c>Video.LinkedVideos.Decide</c> reads
    /// the SAME recorded key back — so it only has to be stable per linked video and to CHANGE when the file does, which
    /// is exactly what makes re-picking a repaired file re-arm it. The linked videos's other two fresh-attach sites (the row
    /// drop in <c>Track.Table.cs</c>, the "play this file" drop in <c>Playlist.Page.cs</c>) already record the path;
    /// this names that convention once so the three cannot drift.</summary>
    static string SourceKeyFor(string path) => path;


    /// <summary>Explorer with the file SELECTED, falling back to its folder when the file is gone. Best-effort: a
    /// missing Explorer or a denied path never throws into the UI thread.</summary>
    static void RevealInExplorer(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                // `/select,` takes ONE argument, quoted as a whole — the comma is part of the switch.
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"")
                    { UseShellExecute = false })?.Dispose();
                return;
            }
            if (Path.GetDirectoryName(path) is { Length: > 0 } dir)
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo { FileName = dir, UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) { Log.Warn(VideoLogCategory, "show in explorer failed", ex); }
    }
}
