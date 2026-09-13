// ── Playback/Playback.Os.cs ────────────────────────────────────────────────────────────────────────────────────────
// SMTC bridge, taskbar button + thumbnail toolbar, jump list, app-icon resolver, media keys, power. Ch 14 is its
// contract — 29 wireframes, 58 parity items
//
// Role: SHELL
// Owner: H
// Wave: 3
// Budget: 840 lines
// Spec: ch 14 §9 (which retracts its own 830; §2 previously said 1,100)
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// WHAT THIS FILE IS. Four OS surfaces, mirrored outward from ONE playback state: the Windows media overlay card
// (SMTC), the taskbar button (overlay icon + progress + thumbnail toolbar), the jump list, and the app-icon resolver
// they share. There is no `Element` tree here and no signal subscription: every surface is a SINK that
// `Playback.Host` calls on the UI thread with `in State`, holding its own last-pushed snapshot in private fields
// (ch 14 §1b's push contract, which replaces the props contract for a surface that has no components).
//
// THE FOUR RULES THAT MAY NOT BE SIMPLIFIED (ch 14 §0):
//   1. Read the STATE, never the engine player. `Playback.State` is the only source still correct while a remote
//      Connect device owns playback; the engine's own NowPlaying is dark then, and a lock screen that goes blank
//      when the user transfers to their phone is the failure this prevents.
//   2. Every OS call is edge-deduped and every high-rate call is coalesced. One `UpdateTimeline` is a WinRT
//      activation plus a cross-process COM RPC (~1 ms) on the UI thread; N queued ticks must cost one, not N. SMTC
//      and the taskbar own SEPARATE `SmtcTimelineCoalescer`s (G's, `Playback.cs`) because their bail-outs differ —
//      one shared latch would let one surface consume the other's scheduled flush and the other would freeze.
//   3. The SMTC timeline is zeroed exactly ONCE per transition into LIVE, then silent — and `TryTake` is called
//      unconditionally, because it is what clears the scheduled bit. A bail-out that skips it wedges the latch
//      armed and the scrub bar stops moving for the rest of the process, with no error anywhere.
//   4. Every OS call is fail-soft. A taskbar that refuses is never worth failing playback over.
//
// WHAT IS DELIBERATELY NOT HERE. The whole toast stack — `ToastEscalator`, `ReleaseNotifier`, `DaylistNotifier`,
// `NotificationPolicy`/`Prefs`, `AppUpdateToasts`, the AUMID + activator registration — is owner I's
// `Platform/Notify.cs` + `Notify.Host.cs` in Wave 4 (ch 14 §9 "Who should own these", A9). The split is the HWND
// line: what a window handle drives is here, in Wave 3; what a notification drives is there, in Wave 4.
//
// MEDIA KEYS ARE NOT SEPARATE CODE (ch 14 §9 says §2 named a feature that does not exist). There is no
// `RegisterHotKey` and no media `WM_APPCOMMAND` arm in Wavee or in the engine — a keyboard media key, a headset's
// AVRCP button and the Win11 flyout all arrive through `SystemMediaControls.ButtonPressed`, which is why
// `IsEnabled = true` is load-bearing: disable the session and the keys go dead. §2b below is that handler.
//
// POWER HAD NO 0.2.9 CODE IN THIS CHAPTER either. §6 is the PLAYBACK half only, over the engine's
// `FluentGpu.WindowsApi.Power.PowerSession`: keep-awake while we are the ones making sound, and the suspend/resume
// pair. The AMBIENT cadence policy (battery/AC → render Hz) is a different concern and stays owner S's
// `Platform.cs`, where its own §9 note already books it.
//
// Rules: UI thread only (C1) — the two inbound callbacks (an SMTC button, a power broadcast) hop through
// `Playback.ToUi` first; no unbounded queue (C8); nothing here reads a wall clock for motion (the memory rule
// `animations-sample-frame-time` — `Playback.FrameNowMs` is the frame clock).

using FluentGpu;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.WindowsApi.Media;
using FluentGpu.WindowsApi.Power;
using FluentGpu.WindowsApi.Shell;

using EngineJumpList = FluentGpu.WindowsApi.Shell.JumpList;

namespace Wavee;

public static partial class Playback
{
    /// <summary>The four OS surfaces playback is mirrored onto.</summary>
    public static class Os
    {
        // ── 0. activation ───────────────────────────────────────────────────────────────────────────────────────────

        static bool s_active, s_activating;

        /// <summary>Wire every surface to the app window and seed them from <paramref name="s"/>. Called once by
        /// <c>Playback.Boot</c> AFTER the window exists — the shell ignores a thumbnail-toolbar add made before the
        /// taskbar button does. Gated on Windows 8 (SMTC does not exist before it) and fail-soft throughout.</summary>
        public static void Activate(nint hwnd, in State s)
        {
            if (s_active || hwnd == 0 || s_activating) return;
            s_activating = true;
            if (!OperatingSystem.IsWindowsVersionAtLeast(8)) return;

            Smtc.Activate(hwnd);
            Taskbar.Activate(hwnd);
            JumpList.Activate();
            PowerPolicy.Activate();

            // AFTER the flag, never before: every sink returns early on `!s_active`, so seeding first is a silent
            // no-op and the button stays blank until the next push (the 0.2.9 shape, TaskbarBridge.cs:69-71).
            s_active = true;
            Publish(in s);
        }

        /// <summary>Tear every surface down on exit: the card leaves the flyout, the taskbar button loses its badge
        /// and its bar, the keep-awake request is dropped. The jump list is deliberately NOT cleared — it is the one
        /// surface meant to outlive the process.</summary>
        public static void Shutdown()
        {
            if (!s_active) return;
            s_active = false;
            PowerPolicy.Shutdown();
            Taskbar.Shutdown();
            Smtc.Shutdown();
        }

        /// <summary>Sign-out teardown (ch 14 DATA GAP 8, VERIFIED: nothing in 0.2.9 was wired to this, so a signed-out
        /// Wavee kept showing the previous account's six recents in the taskbar for anyone who right-clicked it).
        /// Owner I's `Notify.Host.cs` adds the toast half — the armed drop toasts and the image cache — beside it.</summary>
        public static void SignedOut()
        {
            JumpList.Forget();
            try { EngineJumpList.Clear(JumpList.Aumid); } catch { /* fail-soft */ }
        }

        // ── 1. the push contract ────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A state change: metadata, transport status, button enablement, the taskbar overlay and progress
        /// MODE, the thumb buttons, the jump list's dirty bit, the keep-awake request. Driven by
        /// <c>Effects.Smtc</c>.</summary>
        public static void Publish(in State s)
        {
            // The bridges ARM THEMSELVES on first use (`Playback.Host`'s boot contract): the window exists by the time
            // the reducer produces its first effect, and a headless test never produces one at all.
            if (!s_active) { Activate(FluentApp.WindowHandle, in s); return; }   // Activate seeds from `s` itself
            Smtc.OnStateChanged(in s);
            Taskbar.OnStateChanged(in s);
            JumpList.OnStateChanged(in s);
            PowerPolicy.OnStateChanged(in s);
            Tray.Host.Publish(in s);   // the fifth sink, the notification-area icon (Platform/Tray.Host.cs; edge-deduped there)
            // ONE seam by design (`Playback.Host`'s §"seams to owner H"): a card refresh and a timeline tick arrive
            // the same way and the BRIDGES decide the cost, because each owns its own latch and its own bail-out.
            PublishPosition(in s);
        }

        /// <summary>A position tick, at whatever rate the host samples. Both surfaces LATCH it and post ONE flush per
        /// burst; neither makes a COM call here. Driven by <c>Effects.SmtcTimeline</c>.</summary>
        public static void PublishPosition(in State s)
        {
            if (!s_active) return;
            Smtc.OnPositionChanged(in s);
            Taskbar.OnPositionChanged(in s);
        }

        // ── 2. SMTC — the media overlay card, the lock screen, the hardware keys (ch 14 W1-W6) ───────────────────────

        /// <summary>The Windows media overlay card. Three strings, an artwork URL, a status enum, four enable bits and
        /// a timeline — the Shell owns every pixel (ch 14 §2's geometry caveat) and Wavee truncates nothing.</summary>
        public static class Smtc
        {
            static SystemMediaControls? s_smtc;
            static EntityId s_lastId;
            static MediaPlaybackStatus s_lastStatus = (MediaPlaybackStatus)(-1);
            static bool s_lastCanNext, s_lastCanPrev, s_haveEnablement;
            static SmtcTimelineCoalescer s_timeline;
            static bool s_liveCleared;
            static long s_durationMs;
            static bool s_isLive;
            static readonly Action s_flush = Flush;              // cached so the per-tick path allocates nothing

            internal static void Activate(nint hwnd)
            {
                try
                {
                    var smtc = SystemMediaControls.GetForWindow(hwnd);
                    smtc.ButtonDispatcher = ToUi;                  // OS worker thread → UI thread (C1)
                    smtc.ButtonPressed += OnButton;
                    smtc.PositionChangeRequested += OnSeekRequested;
                    smtc.SetEnabledButtons(play: true, pause: true, next: true, previous: true);
                    smtc.PlaybackRate = 1.0;                            // must be > 0 or the flyout refuses the card
                    smtc.IsEnabled = true;                              // …and THIS is what makes the media keys live
                    s_smtc = smtc;
                }
                catch (Exception ex) { Log.Warn("playback", "smtc activate failed", ex); }
            }

            internal static void Shutdown()
            {
                if (s_smtc is not { } smtc) return;
                s_smtc = null;
                // Deliberately NOT ClearDisplay first: the session dies with the process, and clearing would paint a
                // visibly empty card for the frames between the two calls.
                try { smtc.ButtonPressed -= OnButton; } catch { }
                try { smtc.PositionChangeRequested -= OnSeekRequested; } catch { }
                try { smtc.IsEnabled = false; } catch { }
                try { smtc.Dispose(); } catch { }
            }

            internal static void OnStateChanged(in State s)
            {
                if (s_smtc is not { } smtc) return;
                s_durationMs = s.DurationMs;
                s_isLive = s.Live.IsLive;

                // metadata — only on an identity change (W2: a pause re-pushes NOTHING but the status)
                EntityId id = s.CurrentId;
                if (!id.Equals(s_lastId))
                {
                    s_lastId = id;
                    s_liveCleared = false;
                    try
                    {
                        if (!s.HasCurrent) smtc.ClearDisplay();
                        else
                        {
                            var t = new Track(s.Current.Slot);
                            string title = t.Knows(TrackFields.Identity) ? t.Title : "";
                            string artist = FirstArtist(t);            // the FIRST artist only, never a joined list
                            string? album = t.AlbumSlot > 0 ? NullIfEmpty(t.Album.Title) : null;
                            smtc.UpdateDisplay(title, artist, album, NullIfEmpty(ArtUrl(t.ImageId)));
                        }
                    }
                    catch (Exception ex) { Log.Warn("playback", "smtc display failed", ex); }
                }

                // status — Changing is tested BEFORE Playing, so a buffering-while-playing tick reads as a transport
                // in flight (ch 14 W3) rather than as smooth playback.
                MediaPlaybackStatus status = StatusFor(s.HasCurrent, s.Buffering, s.Phase == Phase.Playing);
                if (status != s_lastStatus)
                {
                    s_lastStatus = status;
                    try { smtc.SetPlaybackStatus(status); }
                    catch (Exception ex) { Log.Warn("playback", "smtc status failed", ex); }
                }

                // enablement — play/pause are ALWAYS enabled; only the two skips move (W6)
                bool canNext = s.CanSkipNext, canPrev = s.CanSkipPrev;
                if (!s_haveEnablement || canNext != s_lastCanNext || canPrev != s_lastCanPrev)
                {
                    s_haveEnablement = true;
                    s_lastCanNext = canNext;
                    s_lastCanPrev = canPrev;
                    try { smtc.SetEnabledButtons(play: true, pause: true, next: canNext, previous: canPrev); }
                    catch (Exception ex) { Log.Warn("playback", "smtc buttons failed", ex); }
                }
            }

            internal static void OnPositionChanged(in State s)
            {
                if (s_smtc is null) return;
                s_durationMs = s.DurationMs;
                s_isLive = s.Live.IsLive;
                if (s_timeline.Push(s.PosMs)) ToUi(s_flush);       // the FIRST tick of a burst posts one flush
            }

            static void Flush()
            {
                if (s_smtc is not { } smtc) { s_timeline.TryTake(0, out _); return; }

                // The LIVE arm. A six-hour broadcast has no bar; without the zero-once push the Win11 flyout keeps the
                // PREVIOUS track's 3:47 with the thumb frozen mid-bar for the whole stream. `TryTake` runs first and
                // unconditionally, because it is what clears the scheduled bit (§0.3).
                if (s_durationMs <= 0 && s_isLive)
                {
                    s_timeline.TryTake(s_durationMs, out _);
                    if (ZeroTimelineOnce(s_durationMs, s_isLive, s_liveCleared))
                    {
                        s_liveCleared = true;
                        try { smtc.UpdateTimeline(TimeSpan.Zero, TimeSpan.Zero); }
                        catch (Exception ex) { Log.Warn("playback", "smtc timeline clear failed", ex); }
                    }
                    return;
                }
                s_liveCleared = false;      // …or the first real track after a live stream gets no timeline at all

                if (!s_timeline.TryTake(s_durationMs, out long posMs)) return;
                try { smtc.UpdateTimeline(TimeSpan.FromMilliseconds(posMs), TimeSpan.FromMilliseconds(s_durationMs)); }
                catch (Exception ex) { Log.Warn("playback", "smtc timeline failed", ex); }
            }

            // ── 2b. the transport buttons: media keys, the headset, the lock screen (ch 14 §6) ───────────────────────

            static void OnButton(MediaButton button)
            {
                // ALWAYS-ON attribution. A transport change with nothing on screen is otherwise unexplainable from a
                // log, and this is the one input the user cannot point at afterwards.
                Log.Info("playback", "smtc transport button: " + button);
                switch (button)
                {
                    case MediaButton.Play: Post(Input.Resume(FrameNowMs())); break;
                    case MediaButton.Pause: Post(Input.Pause(FrameNowMs())); break;
                    case MediaButton.Next: Post(Input.Next(FrameNowMs())); break;
                    case MediaButton.Previous: Post(Input.Prev(FrameNowMs())); break;
                    case MediaButton.Stop: Post(Input.Pause(FrameNowMs())); break;   // there is no Stop verb
                    default: break;                                                    // record / FF / rewind / channel
                }
            }

            static void OnSeekRequested(double seconds)
            {
                // The OS hands us SECONDS, once, on the scrub bar's release. It goes through the reducer like every
                // other seek so the seek gate is armed — without it a lock-screen scrub while paused snaps back to
                // the pre-seek position on the next authoritative tick.
                long ms = (long)Math.Round(seconds * 1000.0);
                ms = s_durationMs > 0 ? Math.Clamp(ms, 0, s_durationMs) : Math.Max(0, ms);
                Post(Input.Seek((int)Math.Min(ms, int.MaxValue), FrameNowMs()));
            }
        }

        // ── 3. the taskbar button: overlay, progress, thumbnail toolbar (ch 14 W7-W13) ───────────────────────────────

        /// <summary>The taskbar button's corner badge. THREE states and no more, and Playing carries NONE: the
        /// determinate fill IS the playing cue (ch 14 section 0.4, W7).</summary>
        public enum OverlayKind : byte { None, Pause }

        /// <summary>The taskbar progress MODE — not the value. `Idle` also means "drop every position tick", so a
        /// stale duration can never repaint a bar that should be gone (W9).</summary>
        public enum ProgressKind : byte { Idle, Playing, Paused }

        /// <summary>The three thumb buttons' enablement and which glyph the middle one shows. Pure, so ch 14's
        /// W10-W12 are a table in a test rather than a screenshot. The outer two follow `CanSkipPrev`/`CanSkipNext`
        /// and are deliberately NOT forced false when there is no track — they are whatever the state says.</summary>
        public readonly record struct ThumbState(bool PrevEnabled, bool PlayPauseEnabled, bool NextEnabled, bool ShowPause);

        /// <inheritdoc cref="OverlayKind"/>
        public static OverlayKind OverlayFor(bool hasTrack, bool playing)
            => hasTrack && !playing ? OverlayKind.Pause : OverlayKind.None;

        /// <inheritdoc cref="ProgressKind"/>
        public static ProgressKind ProgressFor(bool hasTrack, bool playing)
            => !hasTrack ? ProgressKind.Idle : playing ? ProgressKind.Playing : ProgressKind.Paused;

        /// <inheritdoc cref="ThumbState"/>
        public static ThumbState ThumbsFor(bool hasTrack, bool playing, bool canPrev, bool canNext)
            => new(canPrev, hasTrack, canNext, playing);

        /// <summary>What the SMTC card's transport says. `Changing` is tested BEFORE `Playing`, so a
        /// buffering-while-playing tick reads as a transport in flight (ch 14 W3) rather than as smooth playback.</summary>
        public static MediaPlaybackStatus StatusFor(bool hasTrack, bool buffering, bool playing)
            => !hasTrack ? MediaPlaybackStatus.Closed
             : buffering ? MediaPlaybackStatus.Changing
             : playing ? MediaPlaybackStatus.Playing
             : MediaPlaybackStatus.Paused;

        /// <summary>Should the SMTC timeline be zeroed right now? Exactly ONCE per transition into LIVE, and never
        /// again while it lasts (ch 14 section 0.3, W4): the failure it prevents is the PREVIOUS track's 3:47 frozen
        /// mid-bar for a six-hour broadcast.</summary>
        public static bool ZeroTimelineOnce(long durationMs, bool isLive, bool alreadyCleared)
            => durationMs <= 0 && isLive && !alreadyCleared;

        /// <summary>The taskbar button. Three states and no more: playing carries the determinate green fill and NO
        /// glyph (the fill IS the playing cue, so a play glyph on top of it is redundant), paused carries the yellow
        /// fill plus the pause glyph, no track carries neither and DROPS every position tick.</summary>
        public static class Taskbar
        {
            internal const int IdPrev = 1, IdPlayPause = 2, IdNext = 3;

            static nint s_hwnd;
            static bool s_on;
            static OverlayKind s_lastOverlay = (OverlayKind)255;
            static ProgressKind s_lastProgress = (ProgressKind)255;
            static bool s_lastCanPrev, s_lastCanNext, s_lastPlaying, s_lastHasTrack, s_haveThumbState, s_thumbsAdded;
            static SmtcTimelineCoalescer s_timeline;
            static long s_durationMs;
            static readonly Action s_flush = Flush;
            static string? s_icoPrev, s_icoPlay, s_icoPause, s_icoNext;

            internal static void Activate(nint hwnd)
            {
                s_hwnd = hwnd;
                s_icoPrev = AppIcon.TaskbarGlyph("prev");
                s_icoPlay = AppIcon.TaskbarGlyph("play");
                s_icoPause = AppIcon.TaskbarGlyph("pause");
                s_icoNext = AppIcon.TaskbarGlyph("next");
                FluentApp.ThumbButtonClicked += OnThumbClick;
                FluentApp.TaskbarButtonCreated += OnTaskbarButtonCreated;
                s_on = true;
                ApplyThumbs(forceAdd: true);
            }

            internal static void Shutdown()
            {
                if (!s_on) return;
                s_on = false;
                FluentApp.ThumbButtonClicked -= OnThumbClick;
                FluentApp.TaskbarButtonCreated -= OnTaskbarButtonCreated;
                try { TaskbarManager.SetOverlayIcon(s_hwnd, null, ""); } catch { }
                try { TaskbarManager.ClearProgress(s_hwnd); } catch { }
            }

            internal static void OnStateChanged(in State s)
            {
                if (!s_on) return;
                bool hasTrack = s.HasCurrent;
                bool playing = s.Phase == Phase.Playing;
                s_durationMs = s.DurationMs;

                OverlayKind overlay = OverlayFor(hasTrack, playing);
                if (overlay != s_lastOverlay)
                {
                    s_lastOverlay = overlay;
                    try
                    {
                        // A null path is an explicit CLEAR, never a throw.
                        if (overlay == OverlayKind.None) TaskbarManager.SetOverlayIcon(s_hwnd, null, "");
                        else TaskbarManager.SetOverlayIcon(s_hwnd, s_icoPause, Loc.Get(Strings.Taskbar.Paused));
                    }
                    catch (Exception ex) { Log.Warn("playback", "taskbar overlay failed", ex); }
                }

                ProgressKind progress = ProgressFor(hasTrack, playing);
                if (progress != s_lastProgress)
                {
                    s_lastProgress = progress;
                    try
                    {
                        switch (progress)
                        {
                            case ProgressKind.Idle: TaskbarManager.ClearProgress(s_hwnd); break;
                            case ProgressKind.Playing: TaskbarManager.SetProgressState(s_hwnd, TaskbarProgressState.Normal); break;
                            default: TaskbarManager.SetProgressState(s_hwnd, TaskbarProgressState.Paused); break;
                        }
                    }
                    catch (Exception ex) { Log.Warn("playback", "taskbar progress state failed", ex); }
                }

                bool canPrev = s.CanSkipPrev, canNext = s.CanSkipNext;
                if (!s_haveThumbState || canPrev != s_lastCanPrev || canNext != s_lastCanNext
                    || playing != s_lastPlaying || hasTrack != s_lastHasTrack)
                {
                    s_haveThumbState = true;
                    s_lastCanPrev = canPrev;
                    s_lastCanNext = canNext;
                    s_lastPlaying = playing;
                    s_lastHasTrack = hasTrack;
                    ApplyThumbs(forceAdd: false);
                }
            }

            internal static void OnPositionChanged(in State s)
            {
                if (!s_on) return;
                // Dropped ENTIRELY while Idle, so a stale duration can never repaint a bar that should be gone (W9).
                if (s_lastProgress == ProgressKind.Idle) return;
                s_durationMs = s.DurationMs;
                if (s_timeline.Push(s.PosMs)) ToUi(s_flush);
            }

            static void Flush()
            {
                if (!s_timeline.TryTake(s_durationMs, out long posMs)) return;
                if (!s_on || s_lastProgress == ProgressKind.Idle) return;
                try { TaskbarManager.SetProgress(s_hwnd, (ulong)Math.Max(0, posMs), (ulong)Math.Max(1, s_durationMs)); }
                catch (Exception ex) { Log.Warn("playback", "taskbar progress failed", ex); }
            }

            /// <summary>The three buttons, ids 1/2/3, of the shell's 7-button cap. Added ONCE per HWND and updated
            /// forever after — the shell forbids a second add. A missing `.ico` is a DEGRADED state, not an error
            /// one: the retry publishes the same three buttons with no glyph, so the tooltips and the clicks still
            /// land (ch 14 W13). `DismissOnClick` stays false, which is what makes prev→prev→next work without
            /// re-hovering the thumbnail.</summary>
            static void ApplyThumbs(bool forceAdd)
            {
                if (!s_on) return;
                ThumbState t = ThumbsFor(s_lastHasTrack, s_lastPlaying, s_lastCanPrev, s_lastCanNext);
                string playPauseTip = Loc.Get(t.ShowPause ? Strings.Taskbar.Pause : Strings.Taskbar.Play);
                var buttons = new[]
                {
                    new ThumbButton(IdPrev, s_icoPrev, Loc.Get(Strings.Taskbar.Previous), t.PrevEnabled),
                    new ThumbButton(IdPlayPause, t.ShowPause ? s_icoPause : s_icoPlay, playPauseTip, t.PlayPauseEnabled),
                    new ThumbButton(IdNext, s_icoNext, Loc.Get(Strings.Taskbar.Next), t.NextEnabled),
                };
                try
                {
                    if (forceAdd || !s_thumbsAdded)
                    {
                        TaskbarManager.SetThumbButtons(s_hwnd, buttons);
                        s_thumbsAdded = true;
                    }
                    else
                    {
                        for (int i = 0; i < buttons.Length; i++) TaskbarManager.UpdateThumbButton(s_hwnd, buttons[i]);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("playback", "taskbar thumbs failed — retrying without glyphs", ex);
                    try
                    {
                        TaskbarManager.SetThumbButtons(s_hwnd,
                            new ThumbButton(IdPrev, null, Loc.Get(Strings.Taskbar.Previous), t.PrevEnabled),
                            new ThumbButton(IdPlayPause, null, playPauseTip, t.PlayPauseEnabled),
                            new ThumbButton(IdNext, null, Loc.Get(Strings.Taskbar.Next), t.NextEnabled));
                        s_thumbsAdded = true;
                    }
                    catch { /* fail-soft: no toolbar this session */ }
                }
            }

            /// <summary>Explorer created — or RE-created — the taskbar button. Drop the add-once latch and add again:
            /// without this arm the toolbar is gone for the rest of the session after any `explorer.exe` restart.</summary>
            static void OnTaskbarButtonCreated()
            {
                if (!s_on) return;
                try { TaskbarManager.NotifyTaskbarButtonCreated(s_hwnd); } catch { }
                s_thumbsAdded = false;
                ApplyThumbs(forceAdd: true);
            }

            /// <summary>A thumb click. The middle button reads the LIVE phase, not the last PUSHED one: the cached
            /// field is a frame stale by the time the click arrives, and two fast clicks would then toggle the same
            /// way twice.</summary>
            static void OnThumbClick(int id)
            {
                switch (id)
                {
                    case IdPrev: Post(Input.Prev(FrameNowMs())); break;
                    case IdPlayPause:
                        Post(PhaseSignal.Peek() == Phase.Playing
                            ? Input.Pause(FrameNowMs())
                            : Input.Resume(FrameNowMs()));
                        break;
                    case IdNext: Post(Input.Next(FrameNowMs())); break;
                }
            }
        }

        // ── 4. the jump list (ch 14 W14-W16) ─────────────────────────────────────────────────────────────────────────

        /// <summary>One row of the "Jump back in" category: a full route KEY (never a raw `spotify:` uri — the label
        /// is a name and the argument is a route), the resolved display name, and the kind for the fourth-step
        /// fallback. The stores compose these, so the jump list never resolves a title itself.</summary>
        public readonly record struct JumpRow(string Route, string Title, byte Kind);

        /// <summary>The taskbar jump list: two standing tasks plus up to six recents, republished as one
        /// Begin/Append/Commit COM transaction — a jump list cannot be edited incrementally, every publish is a full
        /// rebuild.</summary>
        public static class JumpList
        {
            /// <summary>Six rows, the app's own cap — NOT the shell's. `BeginList` hands back the user's own
            /// "number of recent items" setting and nothing reads it, so a machine set to four silently shows four.</summary>
            public const int CategoryCap = 6;

            /// <summary>The TRACK-boundary rebuild floor. A play/pause edge skips it (W15).</summary>
            public const long RebuildMinIntervalMs = 60_000;

            static bool s_on, s_havePlayState, s_lastPlaying, s_haveId;
            static long s_lastRebuildMs;
            static EntityId s_lastId;

            /// <summary>The AUMID the toast layer registered, or null for the process default. The shell keys a custom
            /// destination list BY AUMID: a mismatch writes the list for an identity the taskbar button does not have
            /// and it SILENTLY never appears. Owner I's `Notify.Host.cs` sets this at boot.</summary>
            public static string? Aumid { get; set; }

            /// <summary>The play log's recent contexts, newest first, context-collapsed (owner A's
            /// `Entities/Store.cs`, Wave 1). LATE-BOUND on purpose: the jump list ships in Wave 3 with the category
            /// empty until the store attaches, because `Playback.Boot` runs before either store exists.</summary>
            public static Func<int, JumpRow[]>? RecentContexts { get; set; }

            /// <summary>The nav history's recent surfaces, newest first (owner I's `Shell/Shell.cs`, Wave 4). Same
            /// late bind. The two halves share ONE `seen` key space — the composed ROUTE — so a surface that was both
            /// played and visited appears once.</summary>
            public static Func<int, JumpRow[]>? RecentSurfaces { get; set; }

            internal static void Activate()
            {
                s_on = true;
                s_lastRebuildMs = long.MinValue;
                Rebuild();
            }

            /// <summary>Both stores attach AFTER `Activate`, and each attach earns exactly one rebuild.</summary>
            public static void Attach(Func<int, JumpRow[]>? recentContexts = null,
                Func<int, JumpRow[]>? recentSurfaces = null)
            {
                if (recentContexts is not null) RecentContexts = recentContexts;
                if (recentSurfaces is not null) RecentSurfaces = recentSurfaces;
                if (!s_on) return;
                s_lastRebuildMs = long.MinValue;
                Rebuild();
            }

            internal static void Forget()
            {
                RecentContexts = null;
                RecentSurfaces = null;
                s_haveId = false;
                s_havePlayState = false;
            }

            internal static void OnStateChanged(in State s)
            {
                if (!s_on) return;
                bool playing = s.Phase == Phase.Playing;
                // `_havePlayState` matters: without it the first tick's `false` matches the default `false` and the
                // very first play/pause edge is missed.
                bool playChanged = !s_havePlayState || playing != s_lastPlaying;
                bool trackChanged = !s_haveId || !s.CurrentId.Equals(s_lastId);
                s_havePlayState = true;
                s_haveId = true;
                s_lastPlaying = playing;
                s_lastId = s.CurrentId;
                if (!playChanged && !trackChanged) return;

                // The 60 s floor is for TRACK boundaries only — a skip storm must not hammer `ICustomDestinationList`
                // with a Begin/Append/Commit transaction per skip. A play/pause edge SKIPS the floor, because the
                // Pause/Resume task is the one verb the user compares against the app in front of them.
                if (!playChanged && FrameNowMs() - s_lastRebuildMs < RebuildMinIntervalMs) return;
                Rebuild();
            }

            static void Rebuild()
            {
                if (!s_on) return;
                // No exe path ⇒ publish nothing and keep whatever list the shell already has.
                if (Environment.ProcessPath is not { Length: > 0 } exe) return;
                s_lastRebuildMs = FrameNowMs();

                // Re-read the LIVE phase rather than trusting the cached one: the rebuild is posted, so the cached
                // value can be a frame stale by the time the COM transaction runs.
                bool playing = PhaseSignal.Peek() == Phase.Playing;
                string? appIcon = AppIcon.Path();
                string? glyph = AppIcon.TaskbarGlyph(playing ? "pause" : "play");

                var tasks = new[]
                {
                    new JumpTask(
                        Loc.Get(playing ? Strings.Jumplist.Pause : Strings.Jumplist.Resume), exe,
                        playing ? "wavee://pause" : "wavee://resume", glyph ?? appIcon,
                        Loc.Get(playing ? Strings.Jumplist.PausePlayback : Strings.Jumplist.ResumePlayback)),
                    new JumpTask(Loc.Get(Strings.Jumplist.Search), exe, "wavee://open?route=search", appIcon,
                        Loc.Get(Strings.Jumplist.Search)),
                };

                JumpListItem[] items = BuildCategory(exe, appIcon);
                try { EngineJumpList.SetCategory(Loc.Get(Strings.Jumplist.JumpBackIn), items, tasks, Aumid); }
                catch (Exception ex) { Log.Warn("playback", "jump list publish failed", ex); }
            }

            /// <summary>Up to six rows: the play log first, then the nav history, deduped on the composed route. An
            /// EMPTY array means the heading is not drawn at all — `AppendCategory` is skipped on a zero count — and
            /// that is the correct cold start (ch 14 W16), not something to paper over with a placeholder row.</summary>
            static JumpListItem[] BuildCategory(string exe, string? icon)
            {
                var picked = new JumpRow[CategoryCap];
                int n = Pick(Ask(RecentContexts), Ask(RecentSurfaces), picked);
                if (n == 0) return [];

                var items = new JumpListItem[n];
                for (int i = 0; i < n; i++)
                {
                    JumpRow row = picked[i];
                    string title = row.Title.Length > 0 ? row.Title : KindLabel(row.Kind);
                    // The 5th argument is the shell's TOOLTIP. 0.2.9 put the raw uri there (ch 14 DATA GAP 12); the
                    // route key is the same identity and is at least a thing the user could recognise. Identity for
                    // the removed-items filter is Arguments, not this.
                    items[i] = new JumpListItem(title, exe, "wavee://open?route=" + row.Route, icon, row.Route);
                }
                return items;

                static JumpRow[] Ask(Func<int, JumpRow[]>? source)
                {
                    if (source is null) return [];
                    try { return source(CategoryCap * 3) ?? []; } catch { return []; }
                }
            }

            /// <summary>Fill <paramref name="into"/> from the play log first and the nav history second, deduped on the
            /// composed ROUTE and capped by its length. The two halves share ONE key space on purpose: a surface that
            /// was both played and visited must appear once, and changing either composition silently breaks that.</summary>
            public static int Pick(JumpRow[] playLog, JumpRow[] history, Span<JumpRow> into)
            {
                int count = 0;
                Take(playLog, into, ref count);
                Take(history, into, ref count);
                return count;

                static void Take(JumpRow[] rows, Span<JumpRow> into, ref int count)
                {
                    for (int i = 0; i < rows.Length && count < into.Length; i++)
                    {
                        if (rows[i].Route is not { Length: > 0 } route) continue;
                        bool seen = false;
                        for (int j = 0; j < count; j++)
                        {
                            if (string.Equals(into[j].Route, route, StringComparison.Ordinal)) { seen = true; break; }
                        }
                        if (!seen) into[count++] = rows[i];
                    }
                }
            }

            /// <summary>The fourth fallback when nothing resolved a name: the kind WORD, never a raw uri. Wired to
            /// the eleven `jumplist.*` keys that have sat unused in the catalogue since 0.2.9 hard-coded the English
            /// at the call site (ch 14 DATA GAP 3).</summary>
            public static string KindLabel(byte kind) => (EntityKind)kind switch
            {
                EntityKind.Album => Loc.Get(Strings.Jumplist.KindAlbum),
                EntityKind.Playlist => Loc.Get(Strings.Jumplist.KindPlaylist),
                EntityKind.Artist => Loc.Get(Strings.Jumplist.KindArtist),
                EntityKind.Show => Loc.Get(Strings.Jumplist.KindShow),
                EntityKind.Collection => Loc.Get(Strings.Detail.LikedSongs),
                _ => Loc.Get(Strings.Jumplist.KindApp),
            };
        }

        // ── 5. the app-icon resolver (ch 14 W7; 27 lines in 0.2.9 and no more) ───────────────────────────────────────

        /// <summary>Where the shell's `.ico` files are. `IShellLinkW.SetIconLocation` and `LoadImageW` both take a
        /// FILE, so nothing here extracts a PE resource or asks the package manifest: one deployed multi-resolution
        /// icon beside the exe, and null when it is absent (a dev tree without the content copy is degraded, not
        /// broken).</summary>
        public static class AppIcon
        {
            static string? s_app;
            static bool s_appProbed;

            /// <summary><c>assets/AppIcon/appicon.ico</c>, or null.</summary>
            public static string? Path()
            {
                if (s_appProbed) return s_app;
                s_appProbed = true;
                string p = System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "AppIcon", "appicon.ico");
                s_app = File.Exists(p) ? p : null;
                return s_app;
            }

            /// <summary><c>assets/taskbar/{name}.ico</c>, or null. Four files, resolved four times at activation and
            /// once per jump-list rebuild — human rate, so it is not worth a cache.</summary>
            public static string? TaskbarGlyph(string name)
            {
                string p = System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "taskbar", name + ".ico");
                return File.Exists(p) ? p : null;
            }
        }

        // ── 6. power and idle ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The playback half of the power policy. Two facts and no more: the machine must not sleep while we
        /// are the ones making sound, and a suspend parks playback HERE rather than letting a sleeping laptop forward
        /// a pause to somebody's speaker.</summary>
        public static class PowerPolicy
        {
            static IDisposable? s_awake, s_subscription;
            static bool s_displayHeld;
            static readonly Action s_onSuspend = OnSuspendUi;
            static readonly Action s_onResume = OnResumeUi;

            /// <summary>Overridable in a test; by default it reads the host's own `Playback.VideoActive`. VIDEO needs
            /// the DISPLAY awake, audio only needs the system awake — 0.2.9's bug here was gating on FULLSCREEN, which
            /// let the screen sleep during docked video.</summary>
            public static Func<bool> VideoActive { get; set; } = static () => Playback.VideoActive.Peek();

            /// <summary>Set by the host: may we pause locally on suspend? False when a foreign Connect device owns
            /// playback — a sleeping laptop must never pause the user's phone.</summary>
            public static Func<bool>? CanPauseOnSuspend { get; set; }

            internal static void Activate()
            {
                try
                {
                    s_subscription = PowerSession.Subscribe();
                    PowerSession.Suspending += OnSuspending;
                    PowerSession.Resumed += OnResumed;
                }
                catch (Exception ex) { Log.Warn("playback", "power subscribe failed", ex); }
            }

            internal static void Shutdown()
            {
                try { PowerSession.Suspending -= OnSuspending; } catch { }
                try { PowerSession.Resumed -= OnResumed; } catch { }
                Drop();
                try { s_subscription?.Dispose(); } catch { }
                s_subscription = null;
            }

            internal static void OnStateChanged(in State s)
            {
                bool playing = s.Phase == Phase.Playing && s.RoutesLocal;
                if (!playing) { Drop(); return; }
                bool wantsDisplay = VideoActive();
                if (s_awake is not null && wantsDisplay == s_displayHeld) return;   // re-acquire only on a real change
                Drop();
                try
                {
                    s_awake = PowerSession.KeepAwake(keepDisplayOn: wantsDisplay);
                    s_displayHeld = wantsDisplay;
                }
                catch (Exception ex) { Log.Warn("playback", "keep-awake failed", ex); }
            }

            static void Drop()
            {
                // SetThreadExecutionState is a PER-THREAD flag: acquired on the UI thread, released on the UI thread.
                try { s_awake?.Dispose(); } catch { }
                s_awake = null;
                s_displayHeld = false;
            }

            // A power broadcast lands on an OS worker; both handlers hop first (C1).
            static void OnSuspending() => ToUi(s_onSuspend);

            static void OnResumed() => ToUi(s_onResume);

            static void OnSuspendUi()
            {
                Log.Info("playback", "power: suspending");
                Drop();
                if (CanPauseOnSuspend?.Invoke() ?? true) Post(Input.Pause(FrameNowMs()));
            }

            static void OnResumeUi()
            {
                // A suspended machine loses its server-side device registration even though the socket still looks
                // alive. One Tick is all this file owes the reducer; the re-announce is `Spotify.Connect`'s.
                Log.Info("playback", "power: resumed");
                Post(Input.Tick(FrameNowMs()));
            }
        }

        // ── 7. small shared helpers ──────────────────────────────────────────────────────────────────────────────────

        static string FirstArtist(Track t)
        {
            ReadOnlySpan<int> artists = t.ArtistSlots;
            return artists.Length == 0 ? "" : new Artist(artists[0]).Name;
        }

        /// <summary>The artwork the OS fetches for ITSELF: SMTC is handed a URL and decodes it asynchronously, so
        /// there is no app-side decode and no app-side size (ch 14 DATA GAP 2). An id that is already a url or a file
        /// path passes through, so `--fake` and local files work unchanged.</summary>
        static string ArtUrl(StringId image)
        {
            string id = Entities.Strings.Resolve(image);
            if (id.Length == 0) return "";
            if (id.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                || id.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                || id.Contains(":\\", StringComparison.Ordinal)) return id;
            return "https://i.scdn.co/image/" + id;
        }

        static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
    }
}
