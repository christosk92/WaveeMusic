using System;
using System.IO;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Localization;
using FluentGpu.WindowsApi.Dialogs;
using Wavee.Core;

namespace Wavee;

// The Video ▸ submenu action singletons (see AppAction for the model). Every DECISION is in VideoOverrideUx (pure,
// unit-tested engine-free) and every MUTATION is VideoOverrideService.Attach/Remove — which already normalizes, stats,
// persists, clears the quarantine, logs, and fires the bridge's single NotifyVideoOverrideChanged flow. These actions
// therefore only do the three things a service cannot: run the modal picker, raise the toast (with its Undo), and — for
// the playable that is playing RIGHT NOW — make the change visible by restoring the surface.
public static class VideoActions
{
    /// <summary>The category human-rate UI events log on (store/service events ride "video.local", play-time "playback").</summary>
    const string LogCategory = "ui";

    public static readonly AppAction AttachVideo = new()
    {
        Id = ActionId.AttachVideo, IconKey = ActionIcons.Video,
        Label = static c => Loc.Get(Strings.VideoOverride.Attach),
        IsEnabled = static c => Svc(in c) is not null && Uri(in c) is not null,
        Execute = static c => PickAndAttach(in c, Loc.Get(Strings.VideoOverride.PickTitle), replace: false),
    };

    public static readonly AppAction ReplaceVideo = new()
    {
        Id = ActionId.ReplaceVideo, IconKey = ActionIcons.Replace,
        Label = static c => Loc.Get(Strings.VideoOverride.Replace),
        IsEnabled = static c => Svc(in c) is not null && Uri(in c) is not null,
        Execute = static c => PickAndAttach(in c, Loc.Get(Strings.VideoOverride.PickTitle), replace: true),
    };

    /// <summary>Repair a moved file. Same verb as Replace; only the dialog title and the log event differ — the picker
    /// cannot be told to open at a folder, so the previous path's nearest surviving ancestor is surfaced in the toast
    /// path instead of silently lost (see <see cref="VideoOverrideUx.NearestExistingAncestor"/>).</summary>
    public static readonly AppAction LocateVideo = new()
    {
        Id = ActionId.LocateVideo, IconKey = ActionIcons.Locate,
        Label = static c => Loc.Get(Strings.VideoOverride.Locate),
        IsEnabled = static c => Svc(in c) is not null && Uri(in c) is not null,
        Execute = static c => PickAndAttach(in c, Loc.Get(Strings.VideoOverride.LocateTitle), replace: true, locate: true),
    };

    /// <summary>Detach — applied IMMEDIATELY with no confirmation dialog: it is metadata-only, it never touches the file
    /// on disk, and it is undoable, which is exactly the case where NN/g says undo beats confirm.</summary>
    public static readonly AppAction RemoveVideo = new()
    {
        Id = ActionId.RemoveVideo, IconKey = ActionIcons.Remove, Destructive = true,
        Label = static c => Loc.Get(Strings.VideoOverride.Remove),
        IsEnabled = static c => Svc(in c) is { } svc && Uri(in c) is { } uri && svc.Has(uri),
        Execute = static c =>
        {
            if (Svc(in c) is not { } svc || Uri(in c) is not { } uri) return;
            if (!svc.TryGetActive(uri, out var previous) || !svc.Remove(uri)) return;
            Log(in c, "override.menu.remove", "detached the attached video", uri, previous.Path);
            Toast.Show(Loc.Get(Strings.VideoOverride.Removed), new ToastOptions
            {
                Severity = InfoBarSeverity.Success,
                ActionLabel = Loc.Get(Strings.VideoOverride.Undo),
                OnAction = () => Restore(svc, uri, previous.Path),
            });
        },
    };

    public static readonly AppAction ShowVideoInExplorer = new()
    {
        Id = ActionId.ShowVideoInExplorer, IconKey = ActionIcons.RevealFolder,
        Label = static c => Loc.Get(Strings.VideoOverride.ShowInExplorer),
        IsEnabled = static c => Svc(in c) is { } svc && Uri(in c) is { } uri && svc.TryGetActive(uri, out _),
        Execute = static c =>
        {
            if (Svc(in c) is not { } svc || Uri(in c) is not { } uri) return;
            if (svc.TryGetActive(uri, out var o)) ShellOpen.RevealInExplorer(o.Path);
        },
    };

    // ── the shared attach path (menu picker + drag-drop both land here) ──────────────────────────────────────────────

    static void PickAndAttach(in ActionContext c, string title, bool replace, bool locate = false)
    {
        if (Svc(in c) is not { } svc || Uri(in c) is not { } uri) return;
        string? start = null;
        if (locate && svc.TryGetActive(uri, out var broken))
            start = VideoOverrideUx.NearestExistingAncestor(broken.Path, Directory.Exists);
        // FilePicker is modal + blocking and must run on the UI thread that owns the window — which is exactly where a
        // menu invoke lands (menus close on invoke, so there is no open flyout to fight with).
        string? picked;
        try
        {
            picked = FilePicker.OpenFile(FluentApp.WindowHandle, start is null ? title : title + " — " + start,
                new[] { VideoOverrideUx.PickerFilter(Loc.Get(Strings.VideoOverride.Filter)) });
        }
        catch (Exception ex)
        {
            Toast.Show(ex.Message, new ToastOptions { Severity = InfoBarSeverity.Error });
            return;
        }
        if (picked is null) return;   // cancelled
        Apply(c.S, svc, uri, picked, replace);
    }

    /// <summary>Start a playable in a chosen FORM. This exists because "play it" and "play it as video" used to be the
    /// same call: <c>PlayTrackAsync(uri)</c> carries no form, so the decision fell to
    /// <c>PlaybackBridge.ShouldPlayAsVideo</c> reading the user's STANDING surface intent — and a caller who wanted
    /// video had to know to light the surface first, in the right order, before playing. The versions drawer did not
    /// know that, so its music-video row started the audio track.
    ///
    /// <para>That ordering is no longer a rule callers must remember; it lives inside this one verb.
    /// <see cref="MediaForm.Video"/> is a ONE-PLAY request — the intent is scoped to this uri
    /// (<c>PlaybackBridge.PrimeVideoIntentFor</c>) and dies at the next track boundary, so playing a music video never
    /// leaves the STANDING toggle on for the rest of the queue; only the explicit player-bar/menu toggles do that.
    /// <see cref="MediaForm.Audio"/> turns the standing intent off (no callers yet — kept for symmetry), and
    /// <see cref="MediaForm.Default"/> leaves everything alone — byte-identical to a bare play.</para></summary>
    public static void PlayAs(IPlaybackPlayer? player, PlaybackBridge? bridge, string playableUri, MediaForm form)
    {
        if (playableUri.Length == 0 || player is null) return;
        ApplyForm(bridge, playableUri, form);
        _ = player.PlayTrackAsync(playableUri);
    }

    /// <summary>The same verb for a playable the store does not hold — a dropped local file, which arrives as a
    /// synthetic <see cref="Track"/> rather than a uri.</summary>
    public static void PlayAs(IPlaybackPlayer? player, PlaybackBridge? bridge, Track track, MediaForm form)
    {
        if (player is null) return;
        ApplyForm(bridge, track.Uri, form);
        _ = player.PlayTrackAsync(track);
    }

    /// <summary>Set the surface intent BEFORE the play command. <c>ShouldPlayAsVideo</c> reads the intent as the
    /// playable starts, so setting it afterwards lands on the NEXT track instead of this one — the ordering that
    /// every caller used to have to know. A null bridge (tests, audio-only builds) just plays, exactly like Default.</summary>
    static void ApplyForm(PlaybackBridge? bridge, string uri, MediaForm form)
    {
        if (bridge is not { } b) return;
        if (form == MediaForm.Video) b.PrimeVideoIntentFor(uri);   // one-play scope; a no-op while already watching
        else if (form == MediaForm.Audio && b.VideoActive()) b.TurnVideoOff();
    }

    /// <summary>Validate → attach → toast (with Undo) → make it visible. The ONE entry point the menu picker, the
    /// "Locate…" repair and the row drag-drop all share, so those three can never drift apart.</summary>
    public static void Apply(ActionServices s, VideoOverrideService svc, string uri, string path, bool replace)
    {
        var rejection = VideoOverrideUx.Validate(path, File.Exists);
        if (rejection != VideoAttachRejection.None)
        {
            Toast.Show(Loc.Get(rejection == VideoAttachRejection.NotMp4
                ? Strings.VideoOverride.RejectedNotMp4
                : Strings.VideoOverride.RejectedNotFound), new ToastOptions { Severity = InfoBarSeverity.Error });
            s.Svc?.Log.Event(WaveeLogLevel.Warning, LogCategory, "override.attach.rejected",
                "the picked file was refused",
                fields: [WaveeLogField.Of("path", path), WaveeLogField.Of("reason", rejection.ToString())]);
            return;
        }

        // Snapshot BEFORE the mutation — the uri is the primary key, so a replace overwrites the row we would restore.
        bool had = svc.TryGetActive(uri, out var previous);
        try { svc.Attach(uri, path); }
        catch (Exception ex)
        {
            Toast.Show(ex.Message, new ToastOptions { Severity = InfoBarSeverity.Error });
            return;
        }

        s.Svc?.Log.Event(WaveeLogLevel.Info, LogCategory, had ? "override.menu.replace" : "override.menu.attach",
            had ? "replaced the attached video" : "attached a local video",
            fields: [WaveeLogField.Of("uri", uri), WaveeLogField.Of("path", path)]);

        string previousPath = had ? previous.Path : "";
        Toast.Show(Loc.Get(had || replace ? Strings.VideoOverride.Replaced : Strings.VideoOverride.Attached), new ToastOptions
        {
            Severity = InfoBarSeverity.Success,
            ActionLabel = Loc.Get(Strings.VideoOverride.Undo),
            // Undo restores the PREVIOUS state exactly: the prior attachment on a replace, no attachment on a first attach.
            OnAction = () => Restore(svc, uri, previousPath),
        });
        // Reveal is owned by PlaybackBridge.ApplyVideoOverrideChanged (after has-video commit) — calling ShowVideoAt
        // here raced the posted mutation and opened with Available=None, which double-fired Audio→Video + forced reload.
    }

    /// <summary>Undo: re-attach the previous file, or detach when there was none. Both directions run through the same
    /// service mutations, so the bridge's latch/cache/reload flow fires for the undo exactly as it did for the change.</summary>
    static void Restore(VideoOverrideService svc, string uri, string previousPath)
    {
        try
        {
            if (previousPath is { Length: > 0 }) svc.Attach(uri, previousPath);
            else svc.Remove(uri);
        }
        catch (Exception ex) { Toast.Show(ex.Message, new ToastOptions { Severity = InfoBarSeverity.Error }); return; }
        Toast.Show(Loc.Get(Strings.VideoOverride.Restored), new ToastOptions { Severity = InfoBarSeverity.Informational });
    }

    // ── shared gates ─────────────────────────────────────────────────────────────────────────────────────────────────

    static VideoOverrideService? Svc(in ActionContext c) => c.S.VideoOverrides;

    /// <summary>The single playable this submenu acts on. Multi-select has no meaning here (one file, one playable), so
    /// the whole submenu is absent for it — <see cref="ActionTarget.Single"/> is both the gate and the accessor.</summary>
    static string? Uri(in ActionContext c) => c.Target.Single is { Uri.Length: > 0 } t ? t.Uri : null;

    static void Log(in ActionContext c, string eventId, string message, string uri, string path)
        => c.S.Svc?.Log.Event(WaveeLogLevel.Info, LogCategory, eventId, message,
            fields: [WaveeLogField.Of("uri", uri), WaveeLogField.Of("path", path)]);
}
