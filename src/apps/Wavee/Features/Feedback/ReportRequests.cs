using FluentGpu.Signals;

namespace Wavee;

/// <summary>The "open the report dialog from anywhere" request signal — the same monotonic-counter shape
/// <c>SettingsPage</c> uses for its own cross-component requests (e.g. <c>OpenVideoOverrides</c>): a static bump that
/// <see cref="ReportChrome"/> observes in an effect and turns into an <c>overlay.Open</c>, so About's links, the
/// Diagnostics overflow, the Crash reports card and the <c>wavee://open?route=report</c> deep link can all raise the
/// dialog without any of them holding a reference to the overlay service or to <see cref="ReportChrome"/> itself.</summary>
static class ReportRequests
{
    /// <summary>Bumped by <see cref="Open"/>; <see cref="ReportChrome"/>'s effect is keyed on this value.</summary>
    public static readonly Signal<int> Requested = new(0);

    /// <summary>The kind and prefill for the request <see cref="Requested"/> just bumped. Read ONLY from
    /// <see cref="ReportChrome"/>'s effect, in the same tick the bump is observed — never held across a render.</summary>
    public static ReportKind Kind;
    public static ReportPrefill? Prefill;

    /// <summary>Ask <see cref="ReportChrome"/> to open the report dialog for <paramref name="kind"/>, optionally
    /// prefilled (a specific crash report, a specific past session, or a starting title).</summary>
    public static void Open(ReportKind kind, ReportPrefill? prefill = null)
    {
        Kind = kind;
        Prefill = prefill;
        Requested.Value = Requested.Peek() + 1;
    }
}
