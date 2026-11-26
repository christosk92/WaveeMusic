// ── Shell/Hints.Facts.cs — the trigger facts: small observations fed by the places that already know them ─────────────────────────
//
// Role: CORE (settings rework phase 3, handoff/settings-2026-11-18/DESIGN.md 6.3). Each hint's trigger is one sentence over these facts.
// A fact is a counter, a timestamp or a flag in a plain static field, written by a one-line call at the place that sees the event, read
// by `Hints.Judge`. Nothing allocates and nothing runs per frame or per pointer move: a feed bumps `Hints.Epoch` only when its fact
// crosses the threshold a trigger waits for; a fact that grows with time (lyrics on screen, an audiobook playing) is read against the
// clock when a slot asks, and the slot asks again when it would cross (`RecheckMs`).
//
// WHERE EACH FACT COMES FROM (the one call each):
//   lyrics on screen ........ the lyrics footer's hint slot, mounted in every lyrics surface but the mini player's face, active or parked
//                             (`Hints.UI.cs`, `HintSlot`: the lyrics view itself is the place that knows it is on screen)
//   skips near a track's end  `Playback.StepOne` on a local Next input (the bar, media keys, the taskbar thumb, the tray, the palette)
//   early volume moves ...... `Playback.SetVolume` (only user actions call it: the slider, the keys, the stage, the mini player's wheel)
//   bell opens .............. `Shell.OpenNotificationPanel` (persisted: a lifetime count)
//   quits by the window's ✕ . `Tray.Host.OnClose` on a user close that quits (persisted)
//   launches ................ `Shell.Host` at the launch, where it knows a sign-in launch (StartupTask, --tray) from one by hand; the
//                             sign-in time is the session's Explorer start (NEW narrow hook: no code knew the Windows sign-in time),
//                             read once per launch off the UI thread by the hint watcher (persisted: days)
//   swaps cut short ......... `Shell.RouteCommitted` (every commit, Back and Forward included)
//   play/pause clicks ....... `Playback.TogglePlay` with the bar button's cause ("playerbar.button"; Space and media keys say otherwise)
//   daylist rolled over ..... `Home.ScanDaylist` when a known daylist window is replaced by the next one, while Home is on screen
//   audiobook playing ....... the hint watcher's effect on `Playback.IsPlaying` and `Playback.CurrentId`
//   App mode while Mica ..... the App mode dial's write (`SettingsWrites` → `Hints.OnDialWritten`)
//   cache near its budget ... `ChunkDiskCache.Occupancy` (NEW narrow read: the cache's running byte counter and its effective budget,
//                             no directory walk)
//   the Lossless badge up ... the hint watcher's effect on the player bar's `LosslessFormat` (the host's one rule: the switch, a lossless
//                             stream opened on this device); the tip's slot is mounted only where the theme draws the badge

using System.Diagnostics;
using FluentGpu.Localization;
using Wavee.Theming;

namespace Wavee;

/// <summary>The trigger facts (see the file header).</summary>
public static class HintFacts
{
    static long Now => Environment.TickCount64;
    static int Today => DateOnly.FromDateTime(DateTime.Now).DayNumber;

    // ── session facts (a launch starts them over) ────────────────────────────────────────────────────────────────────────

    static int s_lyricsSurfaces;
    static long s_lyricsMs, s_lyricsSince;
    static int s_skipsNearEnd;
    static readonly EntityId[] s_volumeTracks = new EntityId[HintRules.VolumeEarlyTracks];
    static readonly long[] s_volumeAt = new long[HintRules.VolumeEarlyTracks];
    static int s_volumeNext;
    static long s_lastCommit;
    static int s_swapsCut;
    static int s_playClicks;
    static bool s_daylistRolled, s_appModeWhileMica;
    static long s_audiobookMs, s_audiobookSince;
    static bool s_byHand, s_signInAsked;
    static bool s_mixTurnedOn;   // Mix was turned on in a playlist this session (the lesson's offer)
    static bool s_losslessBadge;   // the bar's Lossless badge has a format to show (the state now, not a count: a reset keeps it)

    /// <summary>Forget the session's facts (the inspector's <c>hint reset</c>).</summary>
    internal static void ResetSession()
    {
        s_lyricsMs = s_skipsNearEnd = s_swapsCut = s_playClicks = 0;
        s_lyricsSince = s_lyricsSurfaces > 0 ? Now : 0;
        Array.Clear(s_volumeTracks);
        Array.Clear(s_volumeAt);
        s_volumeNext = 0;
        s_lastCommit = 0;
        s_daylistRolled = s_appModeWhileMica = s_mixTurnedOn = false;
        s_audiobookMs = 0;
        s_audiobookSince = s_audiobookSince > 0 ? Now : 0;
    }

    // ── feeds ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A lyrics surface came on screen (or went off it, or was parked).</summary>
    public static void LyricsOnScreen(bool on)
    {
        if (on)
        {
            if (s_lyricsSurfaces++ == 0) s_lyricsSince = Now;
            return;
        }
        if (s_lyricsSurfaces == 0) return;
        if (--s_lyricsSurfaces == 0)
        {
            s_lyricsMs += Now - s_lyricsSince;
            s_lyricsSince = 0;
        }
    }

    /// <summary>How long lyrics have been on screen this session.</summary>
    public static long LyricsOnScreenMs => s_lyricsMs + (s_lyricsSince > 0 ? Now - s_lyricsSince : 0);

    /// <summary>A local Next (UI thread, before the step): near the end of a known track, it cuts the tail by hand.</summary>
    public static void Skipped()
    {
        int duration = Playback.DurationMs.Peek();
        if (duration <= 0 || duration - Playback.PositionMs.Peek() > HintRules.SkipNearEndMs) return;
        if (++s_skipsNearEnd == HintRules.SkipsNearEnd) Hints.Bump();
    }

    /// <summary>The person moved the volume (per drag step: a compare, no allocation): early in a track, once per track.</summary>
    public static void VolumeMoved()
    {
        if (Playback.PositionMs.Peek() > HintRules.VolumeEarlyMs) return;
        EntityId id = Playback.CurrentId.Peek();
        if (id.Equals(default)) return;
        for (int i = 0; i < s_volumeTracks.Length; i++)
            if (s_volumeTracks[i].Equals(id)) return;
        bool wasHot = EarlyVolumeTracks() >= HintRules.VolumeEarlyTracks;
        s_volumeTracks[s_volumeNext] = id;
        s_volumeAt[s_volumeNext] = Now;
        s_volumeNext = (s_volumeNext + 1) % s_volumeTracks.Length;
        if (!wasHot && EarlyVolumeTracks() >= HintRules.VolumeEarlyTracks) Hints.Bump();
    }

    /// <summary>The different tracks whose volume was moved early within the window.</summary>
    static int EarlyVolumeTracks()
    {
        long now = Now;
        int n = 0;
        for (int i = 0; i < s_volumeAt.Length; i++)
            if (s_volumeAt[i] > 0 && now - s_volumeAt[i] <= HintRules.VolumeEarlyWindowMs) n++;
        return n;
    }

    /// <summary>The bell's panel opened.</summary>
    public static void BellOpened()
    {
        bool below = HintState.BellOpens < HintRules.BellOpens;
        HintState.BellOpened();
        if (below && HintState.BellOpens >= HintRules.BellOpens) Hints.Bump();
    }

    /// <summary>The window's ✕ (or Alt+F4) quit Wavee (not a hide, not the tray's Quit, not the session ending).</summary>
    public static void QuitByClose()
    {
        if (Platform.Args.Fake) return;   // a demo never persists
        HintState.QuitByClose(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>The launch: <paramref name="atSignIn"/> when Windows started it at sign-in (the StartupTask, the Run value's --tray).
    /// A launch by hand soon after a ✕ quit is a quick relaunch.</summary>
    public static void Launched(bool atSignIn)
    {
        if (Platform.Args.Fake || atSignIn) return;
        s_byHand = true;
        long last = HintState.LastQuitMs;
        if (last > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - last <= HintRules.QuickRelaunchMs) HintState.Relaunched(Today);
    }

    /// <summary>Was this launch by hand within minutes of signing in to Windows? Asked once per launch by the hint watcher: the Explorer
    /// process list is read on a pool thread, the answer is posted back to the UI thread.</summary>
    public static void AskSignInLaunch(Action<Action> post)
    {
        if (!s_byHand || s_signInAsked) return;
        s_signInAsked = true;
        Task.Run(() =>
        {
            long after = LaunchAfterSignInMs();
            if (after >= 0 && after <= HintRules.SignInLaunchMs) post(static () => HintState.SignInLaunch(DateOnly.FromDateTime(DateTime.Now).DayNumber));
        });
    }

    /// <summary>How long after this session's sign-in this process started: Explorer starts at sign-in, so its start time in our session
    /// stands for it. -1 when it cannot be read.</summary>
    static long LaunchAfterSignInMs()
    {
        try
        {
            using var me = Process.GetCurrentProcess();
            int session = me.SessionId;
            DateTime launched = me.StartTime;
            DateTime signIn = DateTime.MaxValue;
            foreach (var explorer in Process.GetProcessesByName("explorer"))
                using (explorer)
                {
                    try
                    {
                        if (explorer.SessionId == session && explorer.StartTime < signIn) signIn = explorer.StartTime;
                    }
                    catch (Exception) { }   // another user's process, or gone: not ours
                }
            return signIn == DateTime.MaxValue || signIn > launched ? -1 : (long)(launched - signIn).TotalMilliseconds;
        }
        catch (Exception) { return -1; }
    }

    /// <summary>A route committed. Another page this soon after the last swap is someone waiting out the hand-over (two commits closer
    /// than <see cref="HintRules.RedirectMs"/> are one action, a redirect, not a person).</summary>
    public static void PageCommitted()
    {
        long now = Now, gap = now - s_lastCommit;
        s_lastCommit = now;
        if (gap >= HintRules.RedirectMs && gap <= HintRules.SwapImpatienceMs && ++s_swapsCut == HintRules.SwapImpatience) Hints.Bump();
    }

    /// <summary>The cause the player bar's play button passes (every theme): a click, as opposed to Space or a media key.</summary>
    public const string PlayButtonCause = "playerbar.button";

    /// <summary>Play/pause was asked for, with its cause.</summary>
    public static void PlayPauseAsked(string cause)
    {
        if (!string.Equals(cause, PlayButtonCause, StringComparison.Ordinal)) return;
        if (++s_playClicks == HintRules.PlayPauseClicks) Hints.Bump();
    }

    /// <summary>Mix was turned on in a playlist (MixMenu.Toggle): the Mix lesson's tip may offer itself at the Mix button.</summary>
    public static void MixOn()
    {
        if (s_mixTurnedOn) return;
        s_mixTurnedOn = true;
        Hints.Bump();
    }

    /// <summary>The first time Mix is turned on (this session).</summary>
    public static bool MixTurnedOn() => s_mixTurnedOn;

    /// <summary>The daylist rolled over to its next part of the day (a known window replaced by the next one).</summary>
    public static void DaylistRolled()
    {
        if (s_daylistRolled || Shell.Current.Peek().Kind != Shell.RouteKind.Home) return;
        s_daylistRolled = true;
        Hints.Bump();
    }

    /// <summary>An audiobook chapter started or stopped playing.</summary>
    public static void AudiobookPlaying(bool on)
    {
        if (on == (s_audiobookSince > 0)) return;
        if (on) s_audiobookSince = Now;
        else
        {
            s_audiobookMs += Now - s_audiobookSince;
            s_audiobookSince = 0;
        }
        Hints.Bump();
    }

    /// <summary>The player bar's Lossless badge came up (a lossless stream opened on this device with the badge's switch on) or went.</summary>
    public static void LosslessBadge(bool on)
    {
        if (on == s_losslessBadge) return;
        s_losslessBadge = on;
        Hints.Bump();
    }

    /// <summary>A dial write that is itself a fact; true when it changed one.</summary>
    internal static bool DialWritten(string dialId)
    {
        if (s_appModeWhileMica || !string.Equals(dialId, "appearance.appMode", StringComparison.Ordinal) || !BackdropIsMica()) return false;
        s_appModeWhileMica = true;
        return true;
    }

    // ── the triggers (design 6.3, one sentence each) ─────────────────────────────────────────────────────────────────────

    /// <summary>#1: this PC can run AI lyrics, the feature is off, the song's lyrics are line-synced or untimed, and the lyrics have been
    /// on screen for 30 s this session.</summary>
    public static bool AiLyricsWanted()
        => LyricsOnScreenMs >= HintRules.LyricsOpenMs
           && !Platform.Settings.Get(Platform.Keys.AiLyricsEnabled)
           && AiLyrics.Current.Peek().Availability == AiLyrics.Availability.Available
           && Lyrics.Store.Doc(Shell.NowPlayingFace.TrackPeek())?.Sync is Lyrics.SyncKind.Line or Lyrics.SyncKind.Unsynced;

    /// <summary>#2: Lossless is offered (a key deriver is installed), the setting is below it, and the connection is not metered.</summary>
    public static bool LosslessOffered()
        => Spotify.Audio.CanDerive && !LosslessChosen() && !Platform.Network.IsMetered;

    public static bool LosslessChosen() => Platform.Settings.Get(Platform.Keys.PlaybackQuality) >= (int)Spotify.Audio.Quality.Lossless;

    /// <summary>#3 (state): the metered cap is what lowers the quality now.</summary>
    public static bool MeteredLowers() => Platform.Network.EffectiveQuality() < Platform.Settings.Get(Platform.Keys.PlaybackQuality);

    /// <summary>#4: crossfade is off (the hint's own state) and the person skipped within a track's last seconds three times this session.</summary>
    public static bool CuttingTails() => s_skipsNearEnd >= HintRules.SkipsNearEnd;

    /// <summary>#5: the person moved the volume early in three different tracks within ten minutes.</summary>
    public static bool UnevenLoudness() => EarlyVolumeTracks() >= HintRules.VolumeEarlyTracks;

    /// <summary>#6 (state): the equalizer's state at rest.</summary>
    public static string EqualizerWords()
        => Loc.Get(Platform.Settings.Get(Platform.Keys.EqualizerEnabled) ? Strings.Hints.EqualizerState.On : Strings.Hints.EqualizerState.Off);

    /// <summary>#7: a topic is set to Windows while Windows delivery is off, and the bell has been opened three times.</summary>
    public static bool WantsBanners()
    {
        if (HintState.BellOpens < HintRules.BellOpens) return false;
        foreach (NotifyTopic topic in Notify.Prefs.AllTopics)
            if (Notify.Prefs.Level(topic) == NotifyLevel.Windows) return true;
        return false;
    }

    /// <summary>#8: Wavee was started again soon after a ✕ quit twice today, and its icon is not set to Never.</summary>
    public static bool QuitsByAccident()
        => HintState.RelaunchesOn(Today) >= HintRules.QuickRelaunchesPerDay
           && Tray.ModeFrom(Platform.Settings.Get(Platform.Keys.TrayIconMode)) != Tray.IconMode.Never;

    /// <summary>#9: Wavee was started by hand within minutes of signing in to Windows on three days.</summary>
    public static bool OpensAtSignIn() => HintState.SignInDays >= HintRules.SignInLaunchDays;

    /// <summary>#10: the person left a page within a moment of its swap five times this session.</summary>
    public static bool WaitsForSwaps() => s_swapsCut >= HintRules.SwapImpatience;

    /// <summary>#11 (WinUI): the person changed App mode while the backdrop is Mica (adjusting the look).</summary>
    public static bool AdjustingTheLook() => s_appModeWhileMica;

    /// <summary>#11's state reached: the backdrop is no longer Mica.</summary>
    public static bool BackdropChanged() => !BackdropIsMica();

    /// <summary>The active theme's Backdrop option is on its first choice, Mica (false where the theme has no such option).</summary>
    static bool BackdropIsMica() => BackdropDial() is { } dial && dial.Current is 0;

    /// <summary>#11 applies: the active theme declares a Backdrop option (WinUI's window material).</summary>
    public static bool BackdropOffered() => BackdropDial() is not null;

    static SettingsDial? BackdropDial()
    {
        foreach (var (_, _, dial) in SettingsSpec.LiveDials)
            if (string.Equals(dial.Id, SettingsSpec.ThemeOptionPrefix + "style.backdrop", StringComparison.Ordinal)) return dial;
        return null;
    }

    /// <summary>#12: the daylist rolled over while Home was on screen.</summary>
    public static bool DaylistRolledOnHome() => s_daylistRolled;

    /// <summary>#13: twenty mouse clicks on play/pause this session.</summary>
    public static bool ClicksPlayALot() => s_playClicks >= HintRules.PlayPauseClicks;

    /// <summary>#14: an audiobook is playing at night and has played twenty minutes.</summary>
    public static bool ListeningAtBedtime()
    {
        int hour = DateTime.Now.Hour;
        return s_audiobookSince > 0 && (hour >= HintRules.BedtimeHour || hour < HintRules.BedtimeEndHour)
               && s_audiobookMs + (Now - s_audiobookSince) >= HintRules.AudiobookPlayedMs;
    }

    /// <summary>#18: the player bar's Lossless badge is up. The slot that asks is the one each theme mounts beside the badge, only where its bar
    /// draws it (no tier drops it there), so "the first time the badge is on screen" is this fact read by that slot.</summary>
    public static bool LosslessBadgeOnScreen() => s_losslessBadge;

    /// <summary>#16: the playback cache is 95 % of its budget (an unlimited cache never fills).</summary>
    public static bool CacheAlmostFull()
    {
        if (Spotify.Audio.DiskCache.IfCreated is not { } cache) return false;
        var o = cache.Occupancy();
        return o.BudgetBytes is long budget && budget > 0 && o.Bytes >= budget * HintRules.CacheFullShare;
    }

    /// <summary>#16's words, with the numbers.</summary>
    public static string CacheWords()
    {
        var o = Spotify.Audio.DiskCache.IfCreated?.Occupancy();
        long used = o?.Bytes ?? 0, budget = o?.BudgetBytes ?? 0;
        return Strings.Hints.CacheFull.Text(Settings.StorageFormat.Bytes(used), Settings.StorageFormat.Bytes(budget));
    }

    /// <summary>When a waiting hint's time-grown fact would cross its threshold (ms from now), or 0 when only an event can make it.</summary>
    public static long RecheckMs(HintDef def)
    {
        if (def.Id == "ai-lyrics" && s_lyricsSince > 0) return Math.Max(0, HintRules.LyricsOpenMs - LyricsOnScreenMs);
        if (def.Id == "sleep-timer" && s_audiobookSince > 0)
            return Math.Max(0, HintRules.AudiobookPlayedMs - (s_audiobookMs + (Now - s_audiobookSince)));
        return 0;
    }
}
