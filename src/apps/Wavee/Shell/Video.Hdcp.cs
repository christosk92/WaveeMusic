// ── Shell/Video.Hdcp.cs ────────────────────────────────────────────────────────────────────────────────────────────────
// a screen that cannot carry HDCP never shows the protected picture: the song plays, the video's place says why, and the picture comes
// back by itself on a screen that can. NAMED PARTIAL of Shell/Video.cs.
//
// Role: SHELL (UI thread; the HDCP query runs on a pool thread)
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// WHY. PlayReady hardware DRM (SL3000) turns HDCP on for the display the picture is on. A display whose connection cannot carry it (an
// HDMI monitor behind a USB-C dock, an adapter) can blank WHOLE while the app goes on rendering, with no error anywhere (owner report
// 2026-11-12: an AOC Q24G4 on a dock went black 0.3 s after `[opm] virtual window over the video`, and stayed black until the dock was
// pulled). The listener cannot even see the window to move it.
//
// WHAT WINDOWS CAN TELL, AND WHEN. No passive API knows whether a SINK can carry HDCP: the GPU only knows its own connector until the
// handshake runs, and every query that reflects the sink (Media Foundation's `hdcp=` feature query, `HdcpSession.SetDesiredMinProtection`,
// OPM's protection level) turns HDCP ON to answer. So the checks are split by what they can cost:
//   1. BEFORE engaging (passive, no handshake): the connection's own record. A display path (monitor + connection: the same monitor behind
//      the dock and plugged in directly are two) on which protection failed before is remembered (persisted, 30 days), and a protected
//      video never engages there again: the song plays and the warning shows. A path with no record is allowed.
//   2. AFTER engaging (the safety net): once a protected picture is up, Media Foundation's HDCP query (`OutputProtection.QueryHdcp`) is
//      asked every 500 ms until it settles (it may say "maybe" for up to ~10 s while HDCP 2.2 negotiates), and the engine's own reports
//      (OPMINFO, a key going OUTPUT_RESTRICTED, MF_E_POLICY_UNSUPPORTED) are listened to. Asked then, the query reports the engagement
//      that is already running instead of starting one. Any "not supported" drops the protected picture on that screen (the reducer moves
//      the row to its song through the video-recovery hand-off, which STOPS the protected session), remembers the path and warns.
//   3. MOVES. The renderer's monitor edge (`DisplayInfo.WindowMonitorChanged`, the `[d3d12.display] monitor=` edge) re-runs (1) for the
//      window the video is in: a screen with no record lifts the block and the row goes back to its video at the carried position (the song
//      plays on while the video attaches under it, then the cut); moving back to a remembered screen blocks again before engaging.
// One always-on line per check and per transition: `[hdcp] monitor=<name> check=<mechanism> result=<...>`, `[hdcp] drop ...`,
// `[hdcp] transition ...`.

using System.Threading;
using System.Threading.Tasks;

using FluentGpu.Pal.Windows;
using FluentGpu.Signals;
using FluentGpu.WindowsApi.Media.PlayReady;

namespace Wavee;

public static partial class Video
{
    /// <summary>The HDCP guard (see the file head): which screen the video is on, whether it can carry protected video, and the block the
    /// reducer and the video surfaces follow.</summary>
    public static class Hdcp
    {
        /// <summary>The screen the video is on cannot carry HDCP: the surfaces show <see cref="JoinVisual.ScreenBlocked"/> for a protected
        /// row, and the reducer plays such a row as its song. UI thread only.</summary>
        public static readonly Signal<bool> Blocked = new(false);

        /// <summary>The persisted record of display paths protection failed on: one <c>path\tunixDay</c> per line.</summary>
        public static readonly SettingKey<string> FailedPathsKey = new("video.hdcp.failedPaths", "");

        /// <summary>How long a failure is remembered for a display path (a fixed dock or cable is tried again after this).</summary>
        public const int RememberDays = 30;

        /// <summary>How often the HDCP query is asked while it says "maybe" (its documentation: never under 250 ms, 500 preferred).</summary>
        public const int PollMs = 500;

        /// <summary>How long a "maybe" may last before the check gives up without a verdict (the documented bound is ~10 s).</summary>
        public const int SettleMs = 12_000;

        const string Category = "video";

        static readonly Dictionary<string, int> s_failed = new(StringComparer.OrdinalIgnoreCase);
        static bool s_installed, s_loaded;
        static long s_mainHwnd;
        static string s_path = "", s_name = "";
        static bool s_engaged;                 // a protected picture is up on the screen s_path names
        static int s_checkGen;                 // bumps to cancel a running HDCP poll
        static bool s_inspect;                 // `state kind=hdcp`: the verdict is the inspector's (no persistence, no real screen)

        /// <summary>Composition (<c>Video.Install</c>): listen to the renderer's monitor edge and the engine's output-protection reports.</summary>
        public static void Install()
        {
            if (s_installed) return;
            s_installed = true;
            DisplayInfo.WindowMonitorChanged += OnMonitorEdge;
            OutputProtection.Failed += OnProtectionFailed;
        }

        /// <summary>Render thread: a presenting window's monitor changed. Only the main window and the video's own window matter.</summary>
        static void OnMonitorEdge(nint hwnd, string deviceName, bool primary)
        {
            if (primary) Volatile.Write(ref s_mainHwnd, hwnd);
            if (!primary && (nuint)hwnd != OutputProtection.VideoWindow) return;   // a menu or tooltip window: not where the video is
            Playback.ToUi(static () => Reevaluate("monitor"));
        }

        /// <summary>Native notifier thread: the engine said the output protection did not take.</summary>
        static void OnProtectionFailed(OutputProtectionNotice n)
        {
            string cause = n.Cause switch
            {
                OutputProtectionCause.OpmInfo => "opminfo",
                OutputProtectionCause.KeyOutputRestricted => "key-output-restricted",
                _ => "policy-unsupported",
            };
            Playback.ToUi(() => Drop(cause + " a=" + n.A + " b=0x" + unchecked((uint)n.B).ToString("X8")));
        }

        /// <summary>The window the video is in: the protected picture's own window once it was placed, else the main window.</summary>
        static nint VideoWindow()
        {
            nuint w = OutputProtection.VideoWindow;
            return w != 0 ? (nint)w : (nint)Volatile.Read(ref s_mainHwnd);
        }

        /// <summary>Check (1), the passive one: which screen the video's window is on and whether its connection is remembered as failing.
        /// Runs on every monitor edge; a same-screen edge is nothing. UI thread.</summary>
        static void Reevaluate(string why)
        {
            if (s_inspect) return;
            nint hwnd = VideoWindow();
            if (hwnd == 0) return;
            DisplayTargetInfo t = DisplayInfo.TargetForWindow(hwnd);
            string path = t.Valid && t.DevicePath.Length > 0 ? t.DevicePath : t.DeviceName;
            if (path.Length == 0 || string.Equals(path, s_path, StringComparison.OrdinalIgnoreCase)) return;
            bool moved = s_path.Length > 0;
            s_path = path;
            s_name = NameOf(in t);
            s_checkGen++;                       // a poll for the screen it left is not this screen's answer
            bool failedBefore = KnownFailed(path);
            Log.Info(Category, $"[hdcp] monitor={s_name} check=memory result={(failedBefore ? "failed-before" : "no-record")} why={why} " +
                               $"tech={t.OutputTechnology} path={path}");
            SetBlocked(failedBefore, failedBefore ? "the connection failed protection before" : moved ? "moved to a screen with no failure on record" : "start");
            // A protected picture that is up and moved with the window to a screen with no record: check it there (its OPM window moved too).
            if (!failedBefore && s_engaged) StartQuery("moved");
        }

        /// <summary>The host observer's edge: a protected picture is up (<paramref name="engaged"/>), or the video host let it go. UI thread.</summary>
        public static void NoteEngaged(bool engaged)
        {
            if (engaged == s_engaged) return;
            s_engaged = engaged;
            if (!engaged) { s_checkGen++; return; }
            if (s_inspect) return;
            if (s_path.Length == 0) Reevaluate("engaged");
            if (!Blocked.Peek()) StartQuery("engaged");
        }

        /// <summary>Check (2), the safety net: Media Foundation's HDCP query on a pool thread every <see cref="PollMs"/> until it settles.</summary>
        static void StartQuery(string why)
        {
            int gen = ++s_checkGen;
            string name = s_name;
            _ = Task.Run(async () =>
            {
                long t0 = Environment.TickCount64;
                HdcpAnswer answer;
                int hr;
                while (true)
                {
                    answer = OutputProtection.QueryHdcp(1, out hr);
                    if (Volatile.Read(ref s_checkGen) != gen) return;
                    if (answer != HdcpAnswer.Pending || Environment.TickCount64 - t0 >= SettleMs) break;
                    await Task.Delay(PollMs).ConfigureAwait(false);
                }
                long ms = Environment.TickCount64 - t0;
                Playback.ToUi(() => OnQueryAnswer(gen, name, why, answer, hr, ms));
            });
        }

        static void OnQueryAnswer(int gen, string name, string why, HdcpAnswer answer, int hr, long ms)
        {
            if (gen != s_checkGen) return;
            string result = answer switch
            {
                HdcpAnswer.Supported => "supported",
                HdcpAnswer.NotSupported => "not-supported",
                HdcpAnswer.Pending => "maybe-timeout",
                _ => "unknown",
            };
            Log.Info(Category, $"[hdcp] monitor={name} check=mf-hdcp-query result={result} hr=0x{unchecked((uint)hr):X8} ms={ms} why={why}");
            if (answer == HdcpAnswer.NotSupported) Drop("mf-hdcp-query");
        }

        /// <summary>The safety net caught a screen that cannot carry the protected picture: remember its connection, release the picture
        /// (the reducer stops the protected session and plays the song) and warn. UI thread.</summary>
        static void Drop(string cause)
        {
            if (s_inspect) return;
            if (s_path.Length == 0) Reevaluate("drop");
            Log.Info(Category, $"[hdcp] drop monitor={s_name} cause={cause} - the protected picture is released on this screen, the song plays");
            if (s_path.Length > 0) Remember(s_path);
            s_checkGen++;
            SetBlocked(true, "protection did not take (" + cause + ")");
        }

        static void SetBlocked(bool blocked, string why)
        {
            if (Blocked.Peek() == blocked) return;
            Blocked.Value = blocked;
            Log.Info(Category, $"[hdcp] transition monitor={s_name} {(blocked ? "blocked" : "cleared")} why={why}");
            Playback.SetVideoScreenBlocked(blocked);
        }

        // ── the record ──────────────────────────────────────────────────────────────────────────────────────────────

        static int Today() => (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 86_400);

        static void Load()
        {
            if (s_loaded) return;
            s_loaded = true;
            string stored = Platform.Settings.Get(FailedPathsKey) ?? "";
            int today = Today();
            foreach (string line in stored.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                int tab = line.LastIndexOf('\t');
                if (tab <= 0 || !int.TryParse(line.AsSpan(tab + 1), out int day) || today - day > RememberDays) continue;
                s_failed[line[..tab]] = day;
            }
        }

        static bool KnownFailed(string path)
        {
            Load();
            return s_failed.TryGetValue(path, out int day) && Today() - day <= RememberDays;
        }

        static void Remember(string path)
        {
            Load();
            s_failed[path] = Today();
            var sb = new System.Text.StringBuilder();
            foreach (var kv in s_failed) sb.Append(kv.Key).Append('\t').Append(kv.Value).Append('\n');
            Platform.Settings.Set(FailedPathsKey, sb.ToString());
        }

        static string NameOf(in DisplayTargetInfo t)
        {
            string device = t.DeviceName.StartsWith(@"\\.\", StringComparison.Ordinal) ? t.DeviceName[4..] : t.DeviceName;
            return t.FriendlyName.Length > 0 ? "\"" + t.FriendlyName + "\"/" + device : device;
        }

        // ── the inspector ───────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>`state kind=hdcp hdcp=blocked|clear`: the verdict a real screen would give, without one. Blocked: the safety net's
        /// drop on a stand-in screen (the same block, the same reducer post, the same surfaces); clear: the move to a screen that can carry
        /// HDCP (the block lifts and the row goes back to its video). Nothing is persisted. UI thread.</summary>
        public static void InspectVerdict(bool blocked)
        {
            s_inspect = true;
            s_name = "\"inspector\"/DISPLAY0";
            s_checkGen++;
            Log.Info(Category, $"[hdcp] monitor={s_name} check=inspector result={(blocked ? "not-supported" : "supported")}");
            if (blocked) Log.Info(Category, $"[hdcp] drop monitor={s_name} cause=inspector - the protected picture is released on this screen, the song plays");
            SetBlocked(blocked, blocked ? "protection did not take (inspector)" : "moved to a screen that carries HDCP (inspector)");
        }

        /// <summary>A mount puts the guard back as the boot left it (no block, no inspector verdict).</summary>
        public static void InspectReset()
        {
            if (!s_inspect) return;
            SetBlocked(false, "inspector reset");
            s_inspect = false;
            s_path = "";
        }
    }
}
