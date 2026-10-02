// ── Screens/Recovery.cs ────────────────────────────────────────────────────────────────────────────────────────────
// Engine-free recovery mode's PURE half (WP-D): the enums, which buttons a given launch offers (`Actions.For`), the
// button-id ↔ action mapping (`ActionFor`), and every English string the dialog shows — verbatim from the approved
// prototype artboard (the crash-dialogs prototype, "Main.dc.html"). A `FluentGpu.Localization.Loc`/`Localization`
// lookup wins when the loc folder loaded (no D3D dependency — see `Platform.Host.cs`'s `HostOpenLocale`, which loads
// the very same `assets/loc` folder this file loads for itself); the consts baked into each accessor below are the
// fallback for when it did not (a corrupted, missing, or not-yet-loaded loc folder must never leave recovery mode
// silent — this is the LAST resort screen, shown when nothing else in the app can be trusted to be working).
//
// Nothing here touches Win32, a window, a process or a `Signal<T>` — see `Recovery.Win32.cs` (same partial class)
// for the half that draws the `TaskDialogIndirect` dialog and runs the native calls. Every fact here is a table or a
// pure string function, not an eyeball check — see `Wavee.Tests/RecoveryTests.cs`.
//
// Role: PLATFORM (engine-free)
// Owner: WP-D
// Spec: docs/plans/wavee/crash-diagnostics-implementation.md §B.8, §D, §F "D · Recovery", §G "Boot loop", §I "WP-D"

using System.Globalization;

namespace Wavee;

/// <summary>The engine-free recovery screen entered by <c>--recovery</c>, a boot loop
/// (<see cref="Crash.RecoveryPolicy"/>), or <c>Platform.Boot()</c> throwing — independent of the engine, the GPU, a
/// login or a profile, because those are exactly the things that might be broken. `App.Main` calls
/// <see cref="Run"/> (Win32.cs) before <c>Platform.Boot</c> ever starts.</summary>
public static partial class Recovery
{
    /// <summary>Why this launch is showing the recovery dialog instead of booting normally.</summary>
    public enum Reason : byte { Switch, BootLoop, BootFailed }

    /// <summary>What <see cref="Run"/> tells its caller (<c>App.Main</c>) to do next.</summary>
    public enum Outcome : byte { StartNormally, Quit }

    /// <summary>One command-link / footer button the dialog can show.</summary>
    public enum ActionKind : byte { Start, Send, Reset, View, OpenFolder, Copy, Quit }

    /// <summary>The `TASKDIALOG_BUTTON`/`MessageBoxW` result ids `Recovery.Win32.cs` assigns each action —
    /// pure integer constants (no Win32 type involved), so <see cref="ActionFor"/> stays a plain, testable mapping.
    /// <see cref="Quit"/> is <c>IDCANCEL</c> deliberately: a custom TaskDialog button may reuse it so Esc / Alt+F4 /
    /// the caption ✕ all resolve to the same action as clicking "Quit" (MS docs, `TaskDialogIndirect`).</summary>
    public static class ButtonIds
    {
        public const int Start = 100, Send = 101, Reset = 102, View = 200, OpenFolder = 201, Copy = 202;
        public const int Quit = 2;   // IDCANCEL
    }

    /// <summary>Which actions a given launch offers, and in the order the dialog lists them: the three command
    /// links first (Start, Send, Reset), then the footer row (View, Open folder, Copy, Quit).</summary>
    public static class Actions
    {
        /// <param name="hasBundle">Whether a crash bundle exists to show, open or copy.</param>
        /// <param name="hasIngest"><see cref="Crash.Uploader.Configured"/> — Send never appears on a dev/E2E build
        /// with no ingest endpoint stamped, even when a bundle exists, matching the in-app prompt's own gate
        /// (§I "WP-C").</param>
        /// <remarks>"Send only when hasBundle &amp;&amp; hasIngest; View/Copy only when hasBundle" (§I "WP-D"). Open
        /// folder is deliberately NOT gated on <paramref name="hasBundle"/>: with no bundle yet (e.g. a
        /// <see cref="Reason.BootFailed"/> launch whose settings/data folder itself could not be opened) it still
        /// opens the crash log root, which is useful evidence even without a specific bundle.</remarks>
        public static IReadOnlyList<ActionKind> For(Reason reason, bool hasBundle, bool hasIngest)
        {
            var list = new List<ActionKind>(7) { ActionKind.Start };
            if (hasBundle && hasIngest) list.Add(ActionKind.Send);
            list.Add(ActionKind.Reset);
            if (hasBundle) list.Add(ActionKind.View);
            list.Add(ActionKind.OpenFolder);
            if (hasBundle) list.Add(ActionKind.Copy);
            list.Add(ActionKind.Quit);
            return list;
        }
    }

    /// <summary>The inverse of <see cref="ButtonIds"/>: what a raw `TaskDialogIndirect`/`MessageBoxW` result id
    /// means. <c>null</c> for any id neither native path can produce (defensive — a dialog that somehow returns an
    /// unknown id must not crash recovery mode; the caller treats <c>null</c> as "show the dialog again").</summary>
    public static ActionKind? ActionFor(int buttonId) => buttonId switch
    {
        ButtonIds.Start => ActionKind.Start,
        ButtonIds.Send => ActionKind.Send,
        ButtonIds.Reset => ActionKind.Reset,
        ButtonIds.View => ActionKind.View,
        ButtonIds.OpenFolder => ActionKind.OpenFolder,
        ButtonIds.Copy => ActionKind.Copy,
        ButtonIds.Quit => ActionKind.Quit,
        _ => null,
    };

    /// <summary>Every string the recovery dialog shows. A property (not a bare const) so each one can prefer a live
    /// `crash.recovery*` loc key over its own English fallback — see <see cref="Loc"/> below. The fallback text is
    /// verbatim from the approved prototype artboard; nothing here is placeholder copy.</summary>
    public static class Text
    {
        public static string Title => Loc.Get("crash.recoveryTitle", "Wavee couldn't start");

        public static string BodySwitch => Loc.Get("crash.recoveryBodySwitch", "You asked Wavee to start in recovery mode.");

        public static string BodyBootFailed => Loc.Get("crash.recoveryBodyBootFailed", "Its settings or data folder could not be opened.");

        /// <summary>The boot-loop body line. <paramref name="count"/> is the consecutive pre-first-frame boot
        /// failures (<see cref="Crash.RecoveryPolicy"/>); the default threshold is 2, which is the prototype's exact
        /// wording ("twice in a row"). Higher counts (a user who kept retrying) generalize rather than repeat "twice".</summary>
        public static string BodyLoop(int count)
        {
            string fallback = count switch
            {
                <= 1 => "It closed unexpectedly before showing its window. Your music and settings are still here.",
                2 => "It closed unexpectedly twice in a row, before showing its window. Your music and settings are still here.",
                _ => string.Format(CultureInfo.InvariantCulture,
                    "It closed unexpectedly {0} times in a row, before showing its window. Your music and settings are still here.",
                    count),
            };
            string key = count switch { <= 1 => "crash.recoveryBodyLoopOnce", 2 => "crash.recoveryBodyLoopTwice", _ => "crash.recoveryBodyLoopMany" };
            return count > 2
                ? Loc.Format(key, fallback, ("count", count))
                : Loc.Get(key, fallback);
        }

        /// <summary>The body line for <paramref name="reason"/>, given this launch's consecutive boot failures
        /// (meaningless outside <see cref="Reason.BootLoop"/>).</summary>
        public static string BodyFor(Reason reason, int bootFailures) => reason switch
        {
            Reason.Switch => BodySwitch,
            Reason.BootFailed => BodyBootFailed,
            _ => BodyLoop(bootFailures),
        };

        public static string StartTitle => Loc.Get("crash.recoveryStartTitle", "Start Wavee");
        public static string StartSubtitle => Loc.Get("crash.recoveryStartSubtitle", "Try again normally");

        public static string SendTitle => Loc.Get("crash.recoverySendTitle", "Send the crash report");
        public static string SendSubtitle => Loc.Get("crash.recoverySendSubtitle", "No account details are included. You can read it first.");

        public static string ResetTitle => Loc.Get("crash.recoveryResetTitle", "Reset Wavee…");
        public static string ResetSubtitle => Loc.Get("crash.recoveryResetSubtitle", "Signs you out and removes all Wavee data on this PC");

        public static string FooterView => Loc.Get("crash.recoveryFooterView", "View report");
        public static string FooterOpenFolder => Loc.Get("crash.recoveryFooterOpenFolder", "Open folder");
        public static string FooterCopy => Loc.Get("crash.recoveryFooterCopy", "Copy");
        public static string FooterQuit => Loc.Get("crash.recoveryFooterQuit", "Quit");

        public static string PrivacyLink => Loc.Get("crash.recoveryPrivacyLink", "Privacy");
        public const string PrivacyUrl = "https://github.com/christosk92/WaveeMusic/blob/main/PRIVACY.md";

        public static string AlwaysSend => Loc.Get("crash.recoveryAlwaysSend", "Always send reports automatically");

        /// <summary>"Report saved 24 Sep 2026, 14:30 · Wavee 0.3.0 · id 3f9c-a1e2" — the prototype's exact
        /// shape, invariant culture (a boot-loop screen must read identically regardless of the OS locale, the same
        /// discipline the fake-seed screenshots use). The report id is shown as its first 8 characters split
        /// 4-4 with a dash, matching the prototype; a shorter id (a test fixture) is shown as-is.</summary>
        public static string LastReport(Crash.BundleInfo bundle)
        {
            string date = bundle.StampLocal.ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture);
            string id = FormatReportId(bundle.Summary.ReportId);
            string fallback = string.Format(CultureInfo.InvariantCulture, "Report saved {0} · Wavee {1} · id {2}",
                date, bundle.Summary.Version, id);
            return Loc.Format("crash.recoveryLastReport", fallback,
                ("date", date), ("version", bundle.Summary.Version), ("id", id));
        }

        static string FormatReportId(string reportId) => reportId.Length >= 8 ? reportId[..4] + "-" + reportId[4..8] : reportId;

        // ── the "Send this report?" confirm (second dialog) ──
        public static string SendConfirmTitle => Loc.Get("crash.recoverySendConfirmTitle", "Send this report?");
        public static string SendConfirmBody => Loc.Get("crash.recoverySendConfirmBody", "No account details are included. You can read it first.");
        public static string IncludeDump => Loc.Get("crash.recoveryIncludeDump", "Include the memory snapshot");
        public static string SendYes => Loc.Get("crash.recoverySendYes", "Send");
        public static string SendNo => Loc.Get("crash.recoverySendNo", "Not now");

        public static string SentResult(string reportId) =>
            Loc.Format("crash.recoverySentResult", "Sent · id {id}", ("id", FormatReportId(reportId)));

        public static string FailedResult(string error) =>
            Loc.Format("crash.recoveryFailedResult", "Couldn't send: {error} — it stays on this PC", ("error", error));

        // ── the reset confirm reuses the EXISTING factory-reset confirm copy (assets/loc/en-US.json,
        //    "settings.storage.factoryResetConfirmTitle"/"…Body") rather than a new crash.recovery* pair. ──
        public static string ResetConfirmTitle => Loc.Get("settings.storage.factoryResetConfirmTitle", "Reset Wavee to a fresh install?");
        public static string ResetConfirmBody => Loc.Get("settings.storage.factoryResetConfirmBody",
            "This signs you out and permanently deletes all local Wavee data on this PC — login, library, metadata, " +
            "settings, playback cache, and history. Wavee will restart on the first-launch screen. The app itself is not uninstalled.");

        public static string ResetFailedTitle => Loc.Get("crash.recoveryResetFailedTitle", "Wavee could not arm the reset");
    }

    /// <summary>The `crash.recovery*` loc lookup: loads <c>assets/loc</c> once (idempotent, best-effort — a failure
    /// here must never take recovery mode down with it) and prefers a live key over the literal fallback every
    /// caller in <see cref="Text"/> already carries. <see cref="FluentGpu.Localization.Localization"/> has no D3D
    /// dependency (see <c>Platform.Host.cs</c>'s <c>HostOpenLocale</c>), so this is safe to call before the engine
    /// or a window exists.</summary>
    static class Loc
    {
        static volatile bool s_loaded;
        static readonly object Gate = new();

        static void EnsureLoaded()
        {
            if (s_loaded) return;
            lock (Gate)
            {
                if (s_loaded) return;
                try { FluentGpu.Localization.Localization.LoadFolder(Path.Combine(AppContext.BaseDirectory, "assets", "loc")); }
                catch { /* the literal fallback still works with no loc folder at all */ }
                s_loaded = true;
            }
        }

        public static string Get(string key, string fallback)
        {
            try
            {
                EnsureLoaded();
                return FluentGpu.Localization.Localization.Has(key) ? FluentGpu.Localization.Localization.Get(key) : fallback;
            }
            catch { return fallback; }
        }

        public static string Format(string key, string fallback, params (string Name, object Value)[] args)
        {
            try
            {
                EnsureLoaded();
                return FluentGpu.Localization.Localization.Has(key) ? FluentGpu.Localization.Localization.Format(key, args) : fallback;
            }
            catch { return fallback; }
        }
    }
}
