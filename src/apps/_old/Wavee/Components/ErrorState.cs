using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;

namespace Wavee;

/// <summary>Friendly ERROR state — never the raw exception. A neutral message + a Retry that re-runs the loader.
/// The technical detail goes to the log (visible in the Diagnostics page), not to the user.
/// <para>Same grammar as <see cref="EmptyState"/>, deliberately: display-face headline, one caption line, ONE quiet
/// action. An error is an empty state with a reason, and giving it its own voice (a 32-DIP critical glyph over a
/// smaller heading, an accent Retry) only meant the two surfaces the user meets most often looked unrelated. The
/// critical COLOUR is gone with the glyph — a red pictogram over "Something went wrong" states the same thing twice,
/// and the accent budget's action rung is not what a recovery button is for.</para></summary>
public static class ErrorState
{
    public static Element Build(Exception? error = null, Action? onRetry = null, string? message = null)
    {
        WaveeLog.Instance.Log(WaveeLogLevel.Warning, "ui",
            error is null ? "Surface error shown" : "Surface error shown: " + error.Message, error);

        return EmptyState.Build(
            message ?? Loc.Get(Strings.Common.ErrorTitle),
            Loc.Get(Strings.Common.ErrorSubtitle),
            onRetry is null ? null : Loc.Get(Strings.Common.Retry),
            onRetry);
    }

    /// <summary>The <see cref="EmptyState.Compact"/> sibling of <see cref="Build"/> — same grammar, same copy, same
    /// log line, just the Subtitle-rung headline instead of PageHero: for a narrow SECTION (a Browse band, a Search
    /// facet body) rather than a whole page, exactly the page-vs-section split <see cref="EmptyState.Compact"/>
    /// itself documents.</summary>
    public static Element Compact(Exception? error = null, Action? onRetry = null, string? message = null)
    {
        WaveeLog.Instance.Log(WaveeLogLevel.Warning, "ui",
            error is null ? "Surface error shown" : "Surface error shown: " + error.Message, error);

        return EmptyState.Compact(
            message ?? Loc.Get(Strings.Common.ErrorTitle),
            Loc.Get(Strings.Common.ErrorSubtitle),
            onRetry is null ? null : Loc.Get(Strings.Common.Retry),
            onRetry);
    }
}
