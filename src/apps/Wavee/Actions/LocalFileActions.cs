using System;
using System.Collections.Generic;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Localization;
using FluentGpu.WindowsApi.Dialogs;
using Wavee.Core;
using Wavee.SpotifyLive.Audio;

namespace Wavee;

// ── "Play a file that isn't in any catalog" — the P4 entry points ────────────────────────────────────────────────────
// Two gestures, one path: pick a file from the profile menu, or drop one on the window. Both build a synthetic Track
// (LocalPlayables) and hand it to the SAME no-hydration verb every other "play this exact track" call site uses —
// IPlaybackPlayer.PlayTrackAsync(Track) — which is the whole point of the source-agnostic seam: no new play path.
//
// A dropped .mp4 takes one extra step first: it attaches to its own generic playable as that playable's video override,
// through VideoActions.Apply (the ONE attach entry point — validate, persist, toast-undo). That is what makes it play
// WITH its embedded audio: the override machinery routes the playable to the video host, which is the only host that
// can pump an mp4's audio (a mounted surface drives the MF session — the invariant P2 documented).
public static class LocalFileActions
{
    const string LogCategory = "ui";

    /// <summary>Can this build play a file at all? The question is "does an audio host exist", not "is there a Spotify
    /// session" — the bridge's <c>LocalPlaybackSupported</c> answers it, and it is true as soon as the audio host is
    /// composed, which the pre-login composition root now does. False only on a build with no local-audio stack. The
    /// affordances are HIDDEN rather than disabled, because an offer you cannot take is worse than no offer.
    ///
    /// <para>The same gate governs the whole <c>Play ▸</c> submenu, not just its "File…" row: a build that cannot play
    /// its own files cannot play a module's stream either, since both end at the same host.</para></summary>
    public static bool CanPlayFiles(ActionServices? s)
        => s?.Svc is not null && s.Playback is { } b && b.LocalPlaybackSupported.Value;

    /// <summary>The "Play file…" command: a modal pick, then play. Runs on the UI thread that owns the window (a menu
    /// invoke always does — the menu closes first, so there is no open flyout to fight with).</summary>
    public static void PickAndPlay(ActionServices? s)
    {
        if (!CanPlayFiles(s)) return;
        string? picked;
        try
        {
            picked = FilePicker.OpenFile(FluentApp.WindowHandle, Loc.Get(Strings.LocalFile.PickTitle),
                new[] { VideoOverrideUx.PlayableFilter(Loc.Get(Strings.LocalFile.Filter)) });
        }
        catch (Exception ex)
        {
            Toast.Show(ex.Message, new ToastOptions { Severity = InfoBarSeverity.Error });
            return;
        }
        if (picked is null) return;   // cancelled
        Play(s!, [picked], "menu");
    }

    /// <summary>A shell-level file drop. Deliberately NOT the row drop: a drop that lands on a track row attaches an
    /// .mp4 to THAT playable (P3) — the engine hands a drop to the deepest accepting target, so a row always wins and
    /// only drops on the rest of the window reach this.</summary>
    public static void PlayDropped(ActionServices? s, IReadOnlyList<string>? paths) => Play(s, paths, "drop");

    static void Play(ActionServices? s, IReadOnlyList<string>? paths, string via)
    {
        if (s is null) return;
        var action = LocalPlayables.ClassifyDrop(paths, out var path);
        if (action == LocalPlayables.DropAction.None)
        {
            Toast.Show(Loc.Get(Strings.LocalFile.Rejected), new ToastOptions { Severity = InfoBarSeverity.Error });
            return;
        }
        if (!CanPlayFiles(s))
        {
            // Reachable only for a drop (the menu item is absent) — the honest answer is the same one every other
            // pre-go-live play intent gets.
            Toast.Show(Loc.Get(Strings.LocalFile.NotReady), new ToastOptions { Severity = InfoBarSeverity.Informational });
            return;
        }

        if (action == LocalPlayables.DropAction.PlayAudio) PlayAudioFile(s, path, via);
        else PlayVideoFile(s, path, via);
    }

    static void PlayAudioFile(ActionServices s, string path, string via)
    {
        var track = LocalPlayables.ForLocalFile(path, LocalAudioDurationProbe.Probe);
        Log(s, "localfile.play", "playing a local audio file", path, via, track.Uri);
        _ = s.Svc!.Player.PlayTrackAsync(track);
    }

    static void PlayVideoFile(ActionServices s, string path, string via)
    {
        // No curation service ⇒ the whole override feature is off (its kill switch), and an mp4 has no audio-host path.
        if (s.VideoOverrides is not { } svc)
        {
            Toast.Show(Loc.Get(Strings.LocalFile.Rejected), new ToastOptions { Severity = InfoBarSeverity.Error });
            return;
        }

        var track = LocalPlayables.ForMedia(path);
        // 1. Self-attach: the file becomes its own playable's video override, through the one shared attach entry point
        //    (validation + persistence + the undo toast all come from there). It is not "current" yet, so Apply's
        //    reveal-if-current step is a no-op — step 2 does that job explicitly for the track we are about to start.
        VideoActions.Apply(s, svc, track.Uri, path, replace: svc.Has(track.Uri));
        if (!svc.Has(track.Uri)) return;   // Apply refused it (validation) and has already explained why

        // 2. Play it AS VIDEO. Dropping a video file to play is an explicit "show me this", which is one of the
        //    gestures allowed to turn the surface intent back on for a user who had closed it — but only for THIS
        //    play (the intent is scoped to the dropped track and expires when playback moves on; only the real
        //    toggles change the standing state). PlayAs owns the ordering that makes it apply to this track.
        Log(s, "localfile.play.video", "playing a dropped video with its own audio", path, via, track.Uri);
        VideoActions.PlayAs(s.Svc!.Player, s.Playback, track, MediaForm.Video);
    }

    static void Log(ActionServices s, string eventId, string message, string path, string via, string uri)
        => s.Svc?.Log.Event(WaveeLogLevel.Info, LogCategory, eventId, message,
            fields: [WaveeLogField.Of("uri", uri), WaveeLogField.Of("path", path), WaveeLogField.Of("via", via)]);
}
