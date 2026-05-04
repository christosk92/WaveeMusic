using System.Diagnostics.Tracing;
using System.Threading;

namespace Wavee.UI.WinUI.Diagnostics;

/// <summary>
/// Custom ETW <see cref="EventSource"/> that emits Start/Stop pairs around every
/// <c>Frame.Navigate</c> call in Wavee. Consumed by the <c>scripts/analyze-trace.ps1</c>
/// fallback pairing logic (see lines 362–381 of that script), which matches on event
/// names containing "Navigating" / "Navigated" within the same <c>(Process, ThreadId)</c>
/// and computes the E2E navigation duration from the time delta.
/// </summary>
/// <remarks>
/// <para>
/// The system XAML provider (<c>Microsoft-Windows-XAML</c>) ships navigation events
/// of its own, but they are extremely sparse in WinUI 3 Desktop traces — our
/// <c>Xaml UI Frame E2E</c> tables routinely export only 2–3 rows. Emitting our own
/// narrowly-scoped pairs means the analyzer's fallback has reliable data to pair,
/// without requiring a newer ADK or different capture profile.
/// </para>
/// <para>
/// The provider GUID is fixed so it matches what we register in
/// <c>scripts/wpa-profiles/WaveeCustomProviders.wprp</c>. Don't regenerate it — if
/// you do, update the WPR profile in lockstep or WPR won't capture the events.
/// </para>
/// <para>
/// When no ETW session is listening (the common case for end users), each call is
/// a cheap <see cref="EventSource.IsEnabled()"/> no-op — safe to leave in release.
/// </para>
/// </remarks>
[EventSource(Name = "Wavee-UI-Navigation", Guid = "6b8c8d6a-3c21-4a07-9f5c-8e7d2f3a9c5b")]
internal sealed class WaveeNavigationEventSource : EventSource
{
    public static readonly WaveeNavigationEventSource Log = new();

    private long _nextNavId;

    /// <summary>
    /// Returns a monotonically increasing navigation id. Start and Stop events
    /// for the same navigation share this id in their payload so an offline
    /// parser can pair them even when multiple tabs navigate concurrently.
    /// </summary>
    public long NextNavId() => Interlocked.Increment(ref _nextNavId);

    /// <summary>
    /// Fires immediately before <c>Frame.Navigate</c> is invoked (or before a
    /// refresh-in-place is applied for the same-page/same-parameter case).
    /// </summary>
    /// <param name="navId">Correlation id from <see cref="NextNavId"/>.</param>
    /// <param name="targetPage">Short type name of the destination page.</param>
    /// <param name="source">Origin of the navigation — "CurrentTab", "NewTab", "Restore", "Suppressed", etc.</param>
    [Event(1, Level = EventLevel.Informational)]
    public void Navigating(long navId, string targetPage, string source)
    {
        if (IsEnabled())
            WriteEvent(1, navId, targetPage ?? string.Empty, source ?? string.Empty);
    }

    /// <summary>
    /// Fires when the Frame finishes navigating (from <c>Frame.Navigated</c>) or
    /// when a refresh-in-place / no-op short-circuit completes.
    /// </summary>
    /// <param name="navId">Correlation id matching the preceding <see cref="Navigating"/>.</param>
    /// <param name="targetPage">Short type name of the destination page.</param>
    [Event(2, Level = EventLevel.Informational)]
    public void Navigated(long navId, string targetPage)
    {
        if (IsEnabled())
            WriteEvent(2, navId, targetPage ?? string.Empty);
    }
}
