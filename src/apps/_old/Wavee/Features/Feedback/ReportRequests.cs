using FluentGpu.Signals;

namespace Wavee;

/// <summary>The "open the report dialog from anywhere" request signal — the same monotonic-counter shape
/// <c>SettingsPage</c> uses for its own cross-component requests (e.g. <c>OpenVideoOverrides</c>): a bump that
/// <see cref="ReportChrome"/> observes in an effect and turns into an <c>overlay.Open</c>, so About's links, the
/// Diagnostics overflow, the Crash reports card and the <c>wavee://open?route=report</c> deep link can all raise the
/// dialog without any of them holding a reference to the overlay service or to <see cref="ReportChrome"/> itself.
///
/// <para>The kind/prefill used to travel out-of-band on two plain static fields next to the counter — two
/// <see cref="Open"/> calls landing in the same flush collapsed to whichever one wrote its fields last, and a stale
/// <see cref="ReportPrefill.CrashReportPath"/> from a previous open was never cleared. The payload now rides IN the
/// signal (a <see cref="ReportRequest"/> per call, stamped with its own monotonic <see cref="ReportRequest.Seq"/>),
/// so every request is a complete, self-contained value — nothing to race, nothing to leave stale.</para></summary>
static class ReportRequests
{
    /// <summary>One "open the report dialog" ask. <see cref="Seq"/> is unique per call (never reused), so
    /// <see cref="ReportChrome"/> can tell "a new request arrived" apart from "the same request re-observed after a
    /// remount" without a sentinel baseline.</summary>
    public sealed record ReportRequest(int Seq, ReportKind Kind, ReportPrefill? Prefill);

    static int _seq;

    /// <summary>The most recent request, or <c>null</c> before the first one. <see cref="ReportChrome"/>'s effect is
    /// keyed on <c>Value?.Seq</c>.</summary>
    public static readonly Signal<ReportRequest?> Requested = new(null);

    /// <summary>Ask <see cref="ReportChrome"/> to open the report dialog for <paramref name="kind"/>, optionally
    /// prefilled (a specific crash report, a specific past session, or a starting title).</summary>
    public static void Open(ReportKind kind, ReportPrefill? prefill = null)
        => Requested.Value = new ReportRequest(++_seq, kind, prefill);
}
