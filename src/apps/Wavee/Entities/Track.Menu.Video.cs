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
// ── WHY THESE FIVE ARE THEIR OWN FILE, AND WHAT THEY ARE ALLOWED TO DO ───────────────────────────────────────────────
//
// Every DECISION is `Video.OverrideUx`'s (pure, engine-free, unit-tested) and every MUTATION is
// `Video.Overrides.Attach/Remove` — which already normalizes, persists, clears the quarantine, bumps the epoch, logs
// and fires the ONE `Changed` flow the host turns into a surface reveal. These verbs therefore do only the three
// things the roster cannot: run the modal picker, raise the toast (with its Undo) and reveal a file in Explorer.
//
// WHICH ROWS EXIST is NOT re-decided here. `VideoItem` (Track.Menu.cs) asks `Video.OverrideUx.MenuFor` and each verb's
// `IsEnabled` asks the SAME function through <see cref="Track.OffersVideoVerb"/>, so a row that is present is always a
// row that runs, and a state nobody offers can never be invoked from a shortcut or the palette either.
//
// ONE THING 0.2.10 DID THAT THIS DOES NOT: pass `replace: true` to choose the toast wording. `Video.Overrides.Attach`
// RETURNS what the roster did (the uri is the primary key, so a duplicate attach IS the replace), so "attached" vs
// "replaced" is read off the mutation instead of guessed by the caller.

using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Localization;
using FluentGpu.WindowsApi.Dialogs;

namespace Wavee;

public readonly partial struct Track
{
    /// <summary>The category these human-rate UI events log on (the roster's own events ride "video.local", play-time
    /// ones "playback").</summary>
    const string VideoLogCategory = "ui";

    // ══ 1. THE SHARED GATE ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Does the Video ▸ submenu offer <paramref name="verb"/> for this playable right now? The ONE gate all
    /// five verbs' <c>IsEnabled</c> predicates share, and it is `Video.OverrideUx.MenuFor` — the same flags
    /// <c>VideoItem</c> builds its rows from, walking the same tier decision playback takes. A row and its verb can
    /// therefore never disagree.
    /// <para>Multi-select has no meaning here (one file, one playable), which is why <see cref="ActionTarget.Single"/>
    /// is both the gate and the accessor: for a selection the whole submenu is absent.</para></summary>
    public static bool OffersVideoVerb(Video.MenuItems verb, string? playableUri)
    {
        if (playableUri is not { Length: > 0 }) return false;
        var which = Video.OverrideUx.MenuFor(true, playableUri, Video.Overrides.Present,
                                             Video.Overrides.Has(playableUri), Video.Overrides.Decide(playableUri).Tier);
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
            Label = static _ => Loc.Get(Strings.VideoOverride.Attach),
            IsEnabled = static c => OffersVideoVerb(Video.MenuItems.Attach, VideoUriOf(c.Target)),
            Execute = static c => PickAndAttach(VideoUriOf(c.Target), Loc.Get(Strings.VideoOverride.PickTitle), locate: false),
        });

        AppActions.Register(new AppAction
        {
            Id = ActionId.ReplaceVideo, IconKey = ActionIcons.Replace,
            Label = static _ => Loc.Get(Strings.VideoOverride.Replace),
            IsEnabled = static c => OffersVideoVerb(Video.MenuItems.Replace, VideoUriOf(c.Target)),
            Execute = static c => PickAndAttach(VideoUriOf(c.Target), Loc.Get(Strings.VideoOverride.PickTitle), locate: false),
        });

        // Repair a moved file. The same mutation as Replace; only the dialog caption and the offered state differ — the
        // picker cannot be told to open AT a folder, so the previous path's deepest surviving ancestor is surfaced in
        // the caption instead of being silently lost (`OverrideUx.NearestExistingAncestor`).
        AppActions.Register(new AppAction
        {
            Id = ActionId.LocateVideo, IconKey = ActionIcons.Locate,
            Label = static _ => Loc.Get(Strings.VideoOverride.Locate),
            IsEnabled = static c => OffersVideoVerb(Video.MenuItems.Locate, VideoUriOf(c.Target)),
            Execute = static c => PickAndAttach(VideoUriOf(c.Target), Loc.Get(Strings.VideoOverride.LocateTitle), locate: true),
        });

        AppActions.Register(new AppAction
        {
            Id = ActionId.ShowVideoInExplorer, IconKey = ActionIcons.RevealFolder,
            Label = static _ => Loc.Get(Strings.VideoOverride.ShowInExplorer),
            IsEnabled = static c => OffersVideoVerb(Video.MenuItems.ShowInExplorer, VideoUriOf(c.Target)),
            Execute = static c =>
            {
                if (VideoUriOf(c.Target) is not { Length: > 0 } uri) return;
                if (Video.Overrides.TryGet(uri, out var o)) RevealInExplorer(o.Path);
            },
        });

        // Detach — applied IMMEDIATELY with no confirmation dialog: it is metadata-only, it never touches the file on
        // disk, and it is undoable, which is exactly the case where undo beats confirm.
        AppActions.Register(new AppAction
        {
            Id = ActionId.RemoveVideo, IconKey = ActionIcons.Remove, Destructive = true,
            Label = static _ => Loc.Get(Strings.VideoOverride.Remove),
            IsEnabled = static c => OffersVideoVerb(Video.MenuItems.Remove, VideoUriOf(c.Target)),
            Execute = static c =>
            {
                if (VideoUriOf(c.Target) is not { Length: > 0 } uri) return;
                // Snapshot BEFORE the mutation — the record is what an Undo restores.
                if (!Video.Overrides.TryGet(uri, out var previous) || !Video.Overrides.Remove(uri)) return;
                Log.Event(WaveeLogLevel.Info, VideoLogCategory, "override.menu.remove", "detached the attached video",
                    fields: [WaveeLogField.Of("uri", uri), WaveeLogField.Of("path", previous.Path)]);
                string path = previous.Path, key = previous.SourceKey;
                Notify.Say(Loc.Get(Strings.VideoOverride.Removed), InfoBarSeverity.Success,
                    Loc.Get(Strings.VideoOverride.Undo), () => RestoreVideo(uri, path, key));
            },
        });
    }

    // ══ 3. THE SHARED PICK → VALIDATE → ATTACH → TOAST PATH ══════════════════════════════════════════════════════════

    /// <summary>Pick a file, validate it, attach it, toast it (with Undo). The ONE path Attach, Replace and the
    /// "Locate…" repair share, so those three can never drift apart. `FilePicker` is modal and blocking and must run on
    /// the thread that owns the window — which is exactly where a menu invoke lands (menus close on invoke, so there is
    /// no open flyout to fight with).</summary>
    static void PickAndAttach(string? playableUri, string title, bool locate)
    {
        if (playableUri is not { Length: > 0 } uri) return;
        bool had = Video.Overrides.TryGet(uri, out var previous);
        string? start = locate && had
            ? Video.OverrideUx.NearestExistingAncestor(previous.Path, Video.Overrides.DirectoryExists)
            : null;

        string? picked;
        try
        {
            picked = FilePicker.OpenFile(FluentApp.WindowHandle, start is null ? title : title + " — " + start,
                                         Video.OverrideUx.PickerFilter(Loc.Get(Strings.VideoOverride.Filter)));
        }
        catch (Exception ex)
        {
            Notify.Say(ex.Message, InfoBarSeverity.Error);                                    // ch 19 item 29
            return;
        }
        if (picked is null) return;                                                            // cancelled: nothing happened

        var rejection = Video.OverrideUx.Validate(picked, Video.Overrides.FileExists);
        if (rejection != Video.AttachRejection.None)
        {
            bool notMp4 = rejection == Video.AttachRejection.NotMp4;
            Notify.Say(Loc.Get(notMp4 ? Strings.VideoOverride.RejectedNotMp4 : Strings.VideoOverride.RejectedNotFound),
                       InfoBarSeverity.Error);                                                 // ch 19 item 30
            Log.Event(WaveeLogLevel.Warning, VideoLogCategory, "override.attach.rejected", "the picked file was refused",
                fields: [WaveeLogField.Of("path", picked), WaveeLogField.Of("reason", notMp4 ? "not-mp4" : "not-found")]);
            return;
        }

        if (Video.Overrides.Attach(uri, picked, SourceKeyFor(picked), NowUnix) is not { } kind)
        {
            // The roster refused what validation had just accepted — the file went away between the two probes. 0.2.10
            // surfaced this as a thrown message (ch 19 item 31); the 0.3 roster returns null instead of throwing, so the
            // honest sentence is the same "couldn't be found" the validator would have said.
            Notify.Say(Loc.Get(Strings.VideoOverride.RejectedNotFound), InfoBarSeverity.Error);  // ch 19 item 31
            return;
        }

        bool replaced = kind == Video.OverrideMutationKind.Replace;
        Log.Event(WaveeLogLevel.Info, VideoLogCategory, replaced ? "override.menu.replace" : "override.menu.attach",
            replaced ? "replaced the attached video" : "attached a local video",
            fields: [WaveeLogField.Of("uri", uri), WaveeLogField.Of("path", picked)]);

        // Undo restores the PREVIOUS state exactly: the prior attachment on a replace, no attachment on a first attach.
        string previousPath = had ? previous.Path : "", previousKey = had ? previous.SourceKey : "";
        Notify.Say(Loc.Get(replaced ? Strings.VideoOverride.Replaced : Strings.VideoOverride.Attached),
                   InfoBarSeverity.Success, Loc.Get(Strings.VideoOverride.Undo),
                   () => RestoreVideo(uri, previousPath, previousKey));                        // ch 19 item 32
        // The REVEAL is the host's (`Video`'s `Changed` handler, after the has-video commit); showing the surface here
        // would race that posted mutation.
    }

    /// <summary>Undo: re-attach the previous file, or detach when there was none. Both directions go through the same
    /// roster mutations, so the host's latch/cache/reload flow fires for the undo exactly as it did for the change.</summary>
    static void RestoreVideo(string uri, string previousPath, string previousSourceKey)
    {
        if (previousPath.Length == 0)
        {
            // Undoing a FIRST attach: detach. Remove is idempotent, so an attachment the user has meanwhile removed
            // themselves still leaves the undo's goal — nothing attached — true.
            Video.Overrides.Remove(uri);
        }
        else if (Video.Overrides.Attach(uri, previousPath, previousSourceKey, NowUnix) is null)
        {
            Notify.Say(Loc.Get(Strings.VideoOverride.RejectedNotFound), InfoBarSeverity.Error);  // ch 19 item 33
            return;
        }
        Notify.Say(Loc.Get(Strings.VideoOverride.Restored), InfoBarSeverity.Informational);      // ch 19 item 34
    }

    /// <summary>The recorded source identity of a local attachment: the PATH. It is one half of the quarantine pair —
    /// the play path writes <c>Video.Overrides.Quarantined(uri, recordedKey)</c> and <c>Video.Overrides.Decide</c> reads
    /// the SAME recorded key back — so it only has to be stable per attachment and to CHANGE when the file does, which
    /// is exactly what makes re-picking a repaired file re-arm it. The roster's other two fresh-attach sites (the row
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
