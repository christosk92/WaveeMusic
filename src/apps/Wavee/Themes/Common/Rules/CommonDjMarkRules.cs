// ── Themes/Common/Rules/CommonDjMarkRules.cs — the AI DJ mark's geometry and fallback colours ─────────────────────────
//
// The DJ's mark as the official client draws it on its "Let DJ pick" button: a disc in the DJ's blue, and on it a thin
// ring in the DJ's green whose top-right segment is filled up to a chord (a stylus on a record). Drawn here as our own vector
// in a 24-unit box, sized to whatever the button is. The colours are the official client's round DJ button in its player bar
// (1.3.10, read off a screenshot on 2026-11-26), not the session's `narration.jump.*` colours, which paint its "Let DJ pick" pill.
// The segment was measured on that screenshot: its chord lies at 0.64 of the ring's radius, centred on the top-right diagonal.

using FluentGpu.Foundation;
using FluentGpu.Render;

namespace Wavee.Themes.Common;

internal static class CommonDjMarkRules
{
    /// <summary>The authored box of the paths below.</summary>
    public const float ViewBox = 24f;

    /// <summary>The disc's and the mark's ARGB.</summary>
    public const uint DiscArgb = 0xFF0A59B8, MarkArgb = 0xFF19E68C;

    /// <summary>The ring's stroke, in view-box units.</summary>
    public const float RingStroke = 1.15f;

    /// <summary>The ring: radius 5.4 round the centre (12, 12).</summary>
    public static readonly PathData Ring =
        PathDataParser.Parse("M6.6 12a5.4 5.4 0 1 0 10.8 0a5.4 5.4 0 1 0 -10.8 0", PathContentEpoch.Mint(), FillRule.NonZero);

    /// <summary>The filled segment at the top right: the ring's outer edge from -99.6° to 9.6°, closed by its chord (3.46 from the centre).</summary>
    public static readonly PathData Segment =
        PathDataParser.Parse("M11 6.1A5.98 5.98 0 0 1 17.9 13Z", PathContentEpoch.Mint(), FillRule.NonZero);
}
