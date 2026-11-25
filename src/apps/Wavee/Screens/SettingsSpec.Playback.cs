// ── Screens/SettingsSpec.Playback.cs — Playback: what plays next, and how songs and videos move on (the dials and their verbs) ─────
//
// Role: CORE (settings rework phase 1). Between songs (autoplay, crossfade bound to the live DSP) · Player bar · Podcasts and
// audiobooks (the episode speed the speed menus set) · Videos and Canvas (quality, the metered cap, prepare-ahead, Canvas, the
// end countdown, the linked videos) · Previews on Home (the Home shelf's preview sound) · Playback runtime (the runtime card's doors).
//
// Every crossfade write persists and then pushes the WHOLE state to `Playback.Audio` (`SetCrossfade`), so the ear follows the slider.

using FluentGpu.Controls;
using FluentGpu.Localization;
using K = Wavee.SettingsRowKind;

namespace Wavee;

public static partial class SettingsSpec
{
    static SettingsCategory PlaybackCategory() => new("playback", Strings.Settings.Categories.Playback.Title,
        Strings.Settings.Categories.Playback.Strip, Strings.Settings.Categories.Playback.Subtitle, "Play",
    [
        Group("betweenSongs", Strings.Settings.Groups.Playback.BetweenSongs, null,
            Make("playback.autoplay", K.Toggle, Strings.Settings.Playback.Autoplay,
                SettingsWriter.OfToggle("audio.autoplay", WriteBool(Platform.Keys.AutoplayEnabled)), legacy: ["autoplay"], glyph: "Play",
                captionKey: Strings.Settings.Playback.AutoplaySub, value: BoolOf(Platform.Keys.AutoplayEnabled),
                contexts: [SettingsSurfaces.QueuePanel, SettingsSurfaces.StageUpNext]),
            // "Your files and Spotify" phase 2 (D2): a Spotify song linked to one of your files plays the file, unless the song has its own choice.
            Make("playback.preferFiles", K.Toggle, Strings.LocalFiles.Link.PreferFiles,
                SettingsWriter.OfToggle("local.link.preferFiles", SettingsVerbs.SetPreferFiles), glyph: "Document",
                captionKey: Strings.LocalFiles.Link.PreferFilesSub, value: BoolOf(LocalLink.PreferFilesKey), aliases: ["local files", "my files", "versions", "flac"]),
            Make("playback.crossfade", K.Toggle, Strings.Settings.Sound.Crossfade,
                SettingsWriter.OfToggle("crossfade.enabled", SettingsVerbs.SetCrossfadeEnabled), legacy: ["crossfade"], glyph: "Audio",
                captionKey: Strings.Settings.Sound.CrossfadeSub, value: BoolOf(Platform.Keys.CrossfadeEnabled), aliases: ["crossfade", "x-fade"],
                contexts: [SettingsSurfaces.QueuePanel, SettingsSurfaces.StageUpNext]),
            // The slider and the number box commit through ONE verb (seconds; the verb snaps to a millisecond).
            Make("playback.crossfade.duration", K.Slider, Strings.Settings.Sound.CrossfadeDuration,
                SettingsWriter.OfSlider("crossfade.duration", v => SettingsVerbs.CommitCrossfade(v)),
                legacy: ["crossfadeDuration"], parent: "playback.crossfade",
                value: s => (float)Settings.Quality.CrossfadeSeconds(s.Get(Platform.Keys.CrossfadeMs)),
                enabled: f => f.CrossfadeEnabled ? SettingsEnablement.On : SettingsEnablement.Off(null), silent: true)),

        Group("playerBar", Strings.Settings.Groups.Playback.PlayerBar, null,
            // Clicking the player bar's (or the mini player's) time flips the same key.
            Make("playback.remainingTime", K.Toggle, Strings.Settings.Playback.ShowRemaining,
                SettingsWriter.OfToggle("playerBar.remaining", WriteBool(Platform.Keys.PlayerBarShowRemaining, _ => Prefs.PlayerBar.Bump())),
                legacy: ["playerBarRemaining"], glyph: "Clock",
                captionKey: Strings.Settings.Playback.ShowRemainingSub, value: BoolOf(Platform.Keys.PlayerBarShowRemaining),
                contexts: [SettingsSurfaces.PlayerBar])),

        Group("podcasts", Strings.Settings.Groups.Playback.Podcasts, null,
            // The episode speed: the speed menus' writer (the account's podcast speed, synced to every device; a song always plays at 1x).
            Make("playback.episodeSpeed", K.Slider, Strings.Player.SpeedMenu,
                SettingsWriter.OfSlider("playback.episodeSpeed", SettingsVerbs.SetEpisodeSpeed), glyph: "Forward",
                value: s => Playback.ValidEpisodeSpeed(s.Get(Playback.EpisodeSpeedKey)),
                contexts: [SettingsSurfaces.PlayerBar, SettingsSurfaces.VideoMenu], aliases: ["1.5x", "2x"])),

        Group("videos", Strings.Settings.Groups.Playback.Videos, null,
            Make("playback.video.quality", K.Choice, Strings.Settings.Playback.VideoQuality,
                SettingsWriter.OfChoice("video.quality", SettingsVerbs.SetVideoQuality), legacy: ["videoQuality"], glyph: "TvMonitor",
                captionKey: Strings.Settings.Playback.VideoQualitySub, value: s => Settings.Quality.VideoIndex(s.Get(Platform.Keys.VideoQuality)),
                contexts: [SettingsSurfaces.VideoMenu], aliases: ["1080p", "720p"], choices: [Strings.Settings.Playback.VideoQualityAuto]),
            Make("playback.video.meteredQuality", K.Choice, Strings.Settings.Playback.VideoMeteredQuality,
                SettingsWriter.OfChoice("video.meteredCap", SettingsVerbs.SetMeteredVideo), legacy: ["videoMetered"], glyph: "RadioTower",
                captionKey: Strings.Settings.Playback.VideoMeteredQualitySub,
                value: s => Settings.Quality.MeteredVideoIndex(s.Get(Platform.Keys.VideoMeteredMaxHeight))),
            // D15's off switch, DEFAULT ON; the write persists and re-decides the keeper's beat immediately.
            Make("playback.video.prepareAhead", K.Toggle, Strings.Settings.Playback.VideoPrepareAhead,
                SettingsWriter.OfToggle("video.prepareAhead", SettingsVerbs.SetVideoPrepareAhead), legacy: ["videoPrepareAhead"], glyph: "Download",
                captionKey: Strings.Settings.Playback.VideoPrepareAheadSub, value: BoolOf(Playback.Video.PrepareAhead)),
            // Spotify Canvas (Shell/Canvas.cs), DEFAULT ON as in Spotify: off, no canvas is asked, fetched or shown anywhere.
            Make("playback.canvas", K.Toggle, Strings.Settings.Playback.ShowCanvas,
                SettingsWriter.OfToggle("playback.canvas", CanvasVideo.SetEnabled), legacy: ["showCanvas"], glyph: "Picture",
                captionKey: Strings.Settings.Playback.ShowCanvasSub, value: BoolOf(CanvasVideo.ShowKey), aliases: ["canvas"]),
            // The full screen's end-of-video countdown (Playback.EndHold.cs), DEFAULT ON: off, a music video's end advances at once, as audio does.
            Make("playback.video.endCountdown", K.Toggle, Strings.Settings.Playback.VideoEndCountdown,
                SettingsWriter.OfToggle("video.endCountdown", SettingsVerbs.SetVideoEndCountdown), legacy: ["videoEndCountdown"], glyph: "Clock",
                captionKey: Strings.Settings.Playback.VideoEndCountdownSub, value: BoolOf(Platform.Keys.VideoEndCountdown)),
            // "Unlink all" is the ONE confirmed verb (N times, no per-row undo). The list is the linked video versions (Shell/Video.LinkedVideos.cs).
            Make("playback.linkedVideos", K.Action, Strings.LinkedVideo.SettingsHeader,
                SettingsWriter.OfAction("video.linked.unlinkAll", SettingsVerbs.ClearAllLinkedVideos), legacy: ["videoOverrides"], glyph: "Edit",
                captionKey: Strings.LinkedVideo.SettingsSub, aliases: ["mp4", "video"],
                enabled: f => f.LinkedVideosPresent ? SettingsEnablement.On : SettingsEnablement.Off(null), silent: true,
                confirm: new SettingsConfirm(Strings.LinkedVideo.ClearAll, Strings.LinkedVideo.ClearAllBody, Strings.LinkedVideo.ClearAll)),
            // The manager flyout's per-item verbs (replace · locate · show in Explorer · unlink) are `SettingsVerbs` members.
            Make("playback.linkedVideos.item", K.Composite, Strings.LinkedVideo.Manage,
                SettingsWriter.Owned("SettingsVerbs.RemoveLinkedVideo/PickLinkedVideo/RevealInExplorer"),
                legacy: ["videoOverrideItem"], parent: "playback.linkedVideos")),

        // Home's song previews (the Spine Shelf and the Release Spotlight play a song's preview on hover, muted by default): their sound, the
        // same writer as the shelf's sound button (`PreviewAudio.SetSound`; the button writes the dial from Home).
        Group("previewsOnHome", Strings.Settings.Groups.Playback.PreviewsOnHome, null,
            Make(HomeUi.PreviewAudio.SoundDial, K.Toggle, Strings.Settings.Playback.PreviewSound,
                SettingsWriter.OfToggle("home.previewSound", HomeUi.PreviewAudio.SetSound), glyph: "Volume",
                captionKey: Strings.Settings.Playback.PreviewSoundSub, value: BoolOf(Platform.Keys.PreviewSound),
                contexts: [SettingsSurfaces.HomeShelf])),

        // The runtime card has no heading of its own in the old pages; its two doors are dials so a theme can draw them anywhere.
        Group("runtime", Strings.Settings.Groups.Playback.Runtime, null,
            Make("playback.runtime", K.Action, Strings.Playback.Runtime.SetUp,
                SettingsWriter.OfAction("runtime.requestOpen", SettingsVerbs.RequestRuntimeSetup), legacy: ["runtimeSetup"]),
            Make("playback.runtime.signature", K.Action, Strings.Playback.Runtime.ViewSignature,
                SettingsWriter.OfAction("runtime.viewSignature", SettingsVerbs.ViewRuntimeSignature),
                legacy: ["runtimeSignature"], parent: "playback.runtime")),
    ]);
}

public static partial class SettingsVerbs
{
    // ── Runtime ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>"Manage" / "Set up" / "Retry setup": open the playback-runtime setup card.</summary>
    public static bool RequestRuntimeSetup()
    {
        Setup.Runtime.RequestOpen();
        return false;
    }

    /// <summary>"View signature": the digital-signature sheet, over the Settings page's overlay host.</summary>
    public static bool ViewRuntimeSignature()
    {
        if (SettingsContext.Overlay is { } overlay) Shell.OpenSignatureDialog(overlay, Setup.Runtime.Status.Peek());
        return false;
    }

    // ── Crossfade ────────────────────────────────────────────────────────────────────────────────────────────────────

    public static void PushCrossfade()
        => Playback.Audio.SetCrossfade(Platform.Settings.Get(Platform.Keys.CrossfadeEnabled), Platform.Settings.Get(Platform.Keys.CrossfadeMs));

    /// <summary>"Prefer my files when I have them" ("Your files and Spotify" phase 2).</summary>
    public static bool SetPreferFiles(bool on)
    {
        LocalLink.SetPreferFiles(on);
        return true;
    }

    public static bool SetCrossfadeEnabled(bool on)
    {
        Platform.Settings.Set(Platform.Keys.CrossfadeEnabled, on);
        PushCrossfade();
        return true;
    }

    /// <summary>The slider and the number box commit here: <c>round(clamp(s, 0, 12) × 1000)</c> ms, persisted and pushed only
    /// when the millisecond value actually moved (a slider drag reports every sample).</summary>
    public static bool CommitCrossfade(double seconds)
    {
        int ms = Settings.Quality.CrossfadeMs(seconds);
        if (Platform.Settings.Get(Platform.Keys.CrossfadeMs) == ms) return false;
        Platform.Settings.Set(Platform.Keys.CrossfadeMs, ms);
        PushCrossfade();
        return true;
    }

    // ── Podcasts and audiobooks ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The episode speed: the speed menus' own writer (<see cref="Playback.SetEpisodeSpeed"/>: clamped, applied, persisted and
    /// synced to the account). The page does not re-read.</summary>
    public static bool SetEpisodeSpeed(float rate)
    {
        Playback.SetEpisodeSpeed(rate);
        return false;
    }

    // ── Video ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Stored as a height (0 = Auto): persisted, then the live pin — the ABR ceiling and the engine's selection.</summary>
    public static bool SetVideoQuality(int index)
    {
        if ((uint)index >= (uint)Settings.Quality.VideoHeights.Length) return false;
        Playback.Video.PinQuality(Settings.Quality.VideoHeights[index]);
        return true;
    }

    /// <summary>Persisted and published through the network policy; the video session folds it on its next quality decision.</summary>
    public static bool SetMeteredVideo(int index)
    {
        if ((uint)index >= (uint)Settings.Quality.MeteredVideoHeights.Length) return false;
        Platform.Network.SetMeteredVideoMaxHeight(Settings.Quality.MeteredVideoHeights[index]);
        return true;
    }

    /// <summary>D15: persist, then re-decide the keeper's beat now rather than at the next track.</summary>
    public static bool SetVideoPrepareAhead(bool on)
    {
        Platform.Settings.Set(Playback.Video.PrepareAhead, on);
        Playback.Video.ApplyPrepareAhead();
        return true;
    }

    /// <summary>The end-of-video countdown: read at the next video's end (Playback.EndHold.cs), so a write needs nothing re-decided now.</summary>
    public static bool SetVideoEndCountdown(bool on)
    {
        Platform.Settings.Set(Platform.Keys.VideoEndCountdown, on);
        return true;
    }

    /// <summary>Unlink every linked video (confirmed first): one log line and one toast. The files stay in the library.</summary>
    public static bool ClearAllLinkedVideos()
    {
        var all = Video.LinkedVideos.All();
        int removed = 0;
        for (int i = 0; i < all.Count; i++)
            if (Video.LinkedVideos.Remove(all[i].Uri)) removed++;
        Log.Event(WaveeLogLevel.Info, "ui", "linkedvideo.settings.clear_all", "unlinked every video",
            fields: [WaveeLogField.Of("count", removed)]);
        Notify.Say(Loc.Get(Strings.LinkedVideo.ClearedAll), InfoBarSeverity.Success);
        return false;
    }

    /// <summary>The manager's "Unlink": unlink one video — applied at once, with a toast carrying Undo, never a dialog.</summary>
    public static void RemoveLinkedVideo(string uri, string path, string sourceKey)
    {
        if (!Video.LinkedVideos.Remove(uri)) return;
        Log.Event(WaveeLogLevel.Info, "ui", "linkedvideo.settings.remove", "unlinked the video",
            fields: [WaveeLogField.Of("uri", uri), WaveeLogField.Of("path", path)]);
        Notify.Say(Loc.Get(Strings.LinkedVideo.Removed), InfoBarSeverity.Success, Loc.Get(Strings.LinkedVideo.Undo),
            () =>
            {
                if (Video.LinkedVideos.Attach(uri, path, sourceKey, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), out var refused) is null)
                    Notify.Say(Video.LinkedVideos.RefusalText(refused), InfoBarSeverity.Error);
            });
    }

    /// <summary>The manager's "Replace" / "Locate": pick, validate (a refusal is an ERROR toast), attach, success toast. Locate
    /// spells the deepest surviving folder into the dialog caption (<paramref name="start"/>).</summary>
    public static void PickLinkedVideo(string uri, string sourceKey, string title, string? start)
    {
        var filter = Video.LinkedVideoUx.PickerFilter(Loc.Get(Strings.LinkedVideo.Filter));
        Pickers.Pick(PickerRequest.Open(start is null ? title : title + " — " + start, filter),
            done: picked => { if (picked is not null) PickedLinkedVideo(uri, sourceKey, picked); },
            failed: ex => Notify.Say(ex.Message, InfoBarSeverity.Error));
    }

    /// <summary><see cref="PickLinkedVideo"/>'s answer, on the UI thread once the dialog (its own thread) has closed.</summary>
    public static void PickedLinkedVideo(string uri, string sourceKey, string picked)
    {
        var rejection = Video.LinkedVideoUx.Validate(picked, Video.LinkedVideos.FileExists);
        if (rejection != Video.AttachRejection.None)
        {
            Notify.Say(Loc.Get(rejection == Video.AttachRejection.NotMp4 ? Strings.LinkedVideo.RejectedNotMp4 : Strings.LinkedVideo.RejectedNotFound), InfoBarSeverity.Error);
            Log.Event(WaveeLogLevel.Warning, "ui", "linkedvideo.attach.rejected", "the picked file was refused",
                fields: [WaveeLogField.Of("path", picked), WaveeLogField.Of("reason", rejection == Video.AttachRejection.NotMp4 ? "not-mp4" : "not-found")]);
            return;
        }
        if (Video.LinkedVideos.Attach(uri, picked, sourceKey, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), out var refused) is null)
        {
            // The file was there a moment ago: say what refused it (it went away, or the library or the link store did not take it).
            Notify.Say(Video.LinkedVideos.RefusalText(refused), InfoBarSeverity.Error);
            Log.Event(WaveeLogLevel.Warning, "ui", "linkedvideo.attach.rejected", "the picked file was refused",
                fields: [WaveeLogField.Of("path", picked), WaveeLogField.Of("reason", refused.ToString())]);
            return;
        }
        Log.Event(WaveeLogLevel.Info, "ui", "linkedvideo.settings.replace", "replaced the linked video",
            fields: [WaveeLogField.Of("uri", uri), WaveeLogField.Of("path", picked)]);
        Notify.Say(Loc.Get(Strings.LinkedVideo.Replaced), InfoBarSeverity.Success);
    }

    /// <summary>"Show in Explorer": select the file. Fail-soft (logged).</summary>
    public static void RevealInExplorer(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("ui", "show in explorer failed", ex); }
    }
}
