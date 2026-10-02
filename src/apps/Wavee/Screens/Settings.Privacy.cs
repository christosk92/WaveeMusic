// ── Screens/Settings.Privacy.cs ────────────────────────────────────────────────────────────────────────────────────
// the Privacy & diagnostics tab's pure decisions: the saved-reports tally, which reports offer "Send…", the hang
// wording's seconds, the sent-time stamp, the shortened install id, the size format and the loc-key choices for a
// report's kind, send state and the reporting mode
//
// Role: CORE
// Owner: R
// Spec: docs/plans/wavee/privacy-diagnostics-tab-implementation.md §5.8
//
// ENGINE-FREE: plain data in, plain data out — no component, no signal, no token, no loc LOOKUP (the keys are returned,
// the UI file resolves them), so `PrivacyDiagnosticsRulesTests` pins every decision over in-memory bundles.

using System.Globalization;

namespace Wavee;

public static partial class Settings
{
    public static class PrivacyRules
    {
        /// <summary>The Saved reports header's three counts plus the bytes the bundles hold on disk.
        /// <c>Sent + NotSent + Closed == Total</c>.</summary>
        public readonly record struct ReportTally(int Total, int Sent, int NotSent, int Closed, long Bytes);

        /// <summary>Sent · not sent · closed unexpectedly. A "closed unexpectedly" bundle (<see cref="Crash.Kind.UncleanExit"/>)
        /// never carries a send state worth reporting, so it is counted only as Closed; of the rest, <see cref="Crash.SendState.Sent"/>
        /// is Sent and everything else (not sent, waiting in the outbox, failed) is NotSent — the evidence is the bundle's own
        /// <c>send.json</c>.</summary>
        public static ReportTally Tally(IReadOnlyList<Crash.BundleInfo> bundles, Func<Crash.BundleInfo, long> bytesOf)
        {
            ArgumentNullException.ThrowIfNull(bundles);
            ArgumentNullException.ThrowIfNull(bytesOf);
            int sent = 0, notSent = 0, closed = 0;
            long bytes = 0;
            for (int i = 0; i < bundles.Count; i++)
            {
                var b = bundles[i];
                bytes += bytesOf(b);
                if (b.Summary.Kind == Crash.Kind.UncleanExit) closed++;
                else if (b.Send.State == Crash.SendState.Sent) sent++;
                else notSent++;
            }
            return new ReportTally(bundles.Count, sent, notSent, closed, bytes);
        }

        /// <summary>Does a saved report offer "Send…"? Only on a build that can send, never for a "closed unexpectedly"
        /// bundle (there is nothing to send — no exception, no dump), and only while the report has not gone out and is
        /// not already waiting in the outbox.</summary>
        public static bool CanSend(Crash.Kind kind, Crash.SendState state, bool configured)
            => configured && kind != Crash.Kind.UncleanExit && state is Crash.SendState.NotSent or Crash.SendState.Failed;

        /// <summary>Pulls the seconds back out of <c>Crash.Handler.WriteHangBundle</c>'s "no UI heartbeat for N s" wording.
        /// Any other shape (a future handler change, a synthetic fixture, a negative or fractional number, a number with no
        /// unit after it) is null — the caller falls back to the raw text rather than guessing.</summary>
        public static int? HangSeconds(string exceptionMessage)
        {
            if (string.IsNullOrEmpty(exceptionMessage)) return null;
            int forIdx = exceptionMessage.LastIndexOf("for ", StringComparison.Ordinal);
            if (forIdx < 0) return null;
            int start = forIdx + 4, end = exceptionMessage.IndexOf(' ', start);
            if (end > start && int.TryParse(exceptionMessage.AsSpan(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out int seconds))
                return seconds;
            return null;
        }

        /// <summary>The "HH:mm" a report was sent at, in the caller's UTC offset. <c>send.json</c>'s
        /// <see cref="Crash.SendRecord.SentAtUtc"/> is an ISO instant; an unparsable stamp (never expected from
        /// <c>Crash.Uploader</c>'s own writer) degrades to its raw text rather than throwing into a settings render, and a
        /// missing one is empty.</summary>
        public static string SentTimeLocal(string? sentAtUtc, TimeSpan utcOffset)
        {
            if (string.IsNullOrEmpty(sentAtUtc)) return "";
            if (DateTime.TryParse(sentAtUtc, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc))
                return utc.Add(utcOffset).ToString("HH:mm", CultureInfo.InvariantCulture);
            return sentAtUtc;
        }

        /// <summary>The first four and last four characters of the install id around an ellipsis; a short id is shown whole.</summary>
        public static string ShortInstallId(string id) => id.Length > 10 ? id[..4] + "…" + id[^4..] : id;

        /// <summary>Binary megabytes, at most one decimal, invariant: "2 MB", "0.4 MB".</summary>
        public static string Mb(long bytes) => (bytes / 1_048_576.0).ToString("0.#", CultureInfo.InvariantCulture) + " MB";

        /// <summary>Whole binary megabytes as a bare number (the "Log files" card's <c>{mb}</c> / <c>{cap}</c> placeholders,
        /// whose sentence supplies the unit): 10 485 760 → "10".</summary>
        public static string WholeMb(long bytes) => (bytes / 1_048_576).ToString(CultureInfo.InvariantCulture);

        /// <summary>The loc key of a report's kind label: Crash (managed or native), Hang, Exit, Closed.</summary>
        public static string KindKey(Crash.Kind kind) => kind switch
        {
            Crash.Kind.Hang => Strings.Settings.Privacy.Reports.KindHang,
            Crash.Kind.ExitCode => Strings.Settings.Privacy.Reports.KindExit,
            Crash.Kind.UncleanExit => Strings.Settings.Privacy.Reports.KindClosed,
            _ => Strings.Settings.Privacy.Reports.KindCrash,
        };

        /// <summary>The loc key of a report's send state, or null when the row shows a dash ("closed unexpectedly" has no
        /// send state). <see cref="Crash.SendState.Sent"/> answers the PARAMETERISED key (its text carries the time), so the
        /// caller formats that one with <c>Strings.Settings.Privacy.Reports.StateSent(time)</c> instead of <c>Loc.Get</c>.</summary>
        public static string? StateKey(Crash.Kind kind, Crash.SendState state)
        {
            if (kind == Crash.Kind.UncleanExit) return null;
            return state switch
            {
                Crash.SendState.Sent => Strings.Settings.Privacy.Reports.StateSentKey,
                Crash.SendState.Queued => Strings.Settings.Privacy.Reports.StateQueued,
                Crash.SendState.Failed => Strings.Settings.Privacy.Reports.StateFailed,
                _ => Strings.Settings.Privacy.Reports.StateNotSent,
            };
        }

        /// <summary>The loc key of the (EFFECTIVE) reporting mode's name — what the Crash reports header's tag shows.</summary>
        public static string ModeKey(Crash.Reporting effective) => effective switch
        {
            Crash.Reporting.Ask => Strings.Settings.Privacy.Facts.ModeAsk,
            Crash.Reporting.Auto => Strings.Settings.Privacy.Facts.ModeAuto,
            _ => Strings.Settings.Privacy.Facts.ModeOff,
        };
    }
}
