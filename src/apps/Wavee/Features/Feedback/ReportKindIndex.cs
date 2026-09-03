namespace Wavee;

/// <summary>The report-kind ↔ <c>Segmented</c>-index mapping, pulled out of <c>ReportDialogBody</c>'s private
/// <c>KindIndex</c>/<c>IndexToKind</c> switches into one engine-free, source-included seam — so the round trip is
/// pinned by a real unit test (<c>ReportKindIndexTests</c>) instead of only ever exercised by eye against the live
/// dialog.</summary>
public static class ReportKindIndex
{
    /// <summary>The kinds the Segmented control offers, in display order — the same order
    /// <c>ReportDialogBody.Render</c> lists <c>Strings.Report.KindBug</c> / <c>KindFeature</c> / <c>KindQuestion</c> /
    /// <c>KindIdea</c>. <see cref="ReportKind.Crash"/> is never here: it is a fixed kind <c>ReportDialog.Open</c>
    /// forces from a crash prompt/prefill, never a value the switch can select.</summary>
    public static readonly ReportKind[] Segments =
    [
        ReportKind.Bug,
        ReportKind.Feature,
        ReportKind.Question,
        ReportKind.Idea,
    ];

    /// <summary>The Segmented index for <paramref name="kind"/>. Falls back to 0 (Bug) for <see cref="ReportKind.Crash"/>
    /// or any kind not in <see cref="Segments"/> — the control must never be asked to select an index it doesn't have.</summary>
    public static int IndexOf(ReportKind kind)
    {
        int i = System.Array.IndexOf(Segments, kind);
        return i < 0 ? 0 : i;
    }

    /// <summary>The kind at a Segmented index. Out-of-range clamps to Bug — same fallback as <see cref="IndexOf"/>,
    /// so the round trip is stable even if the control ever reports a stale/clamped selection.</summary>
    public static ReportKind KindAt(int index)
        => index >= 0 && index < Segments.Length ? Segments[index] : ReportKind.Bug;
}
