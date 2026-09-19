using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee;

// R3.1.4 — THE ONE sidebar count badge.
//
// The user rejected the accent pill outright: a library shortcut's item count is ambient information, not a notification,
// and `InfoBadge.Count` renders WinUI's 16-DIP accent-filled pill with on-accent text — five of those stacked down the
// pane read as five alerts. The quiet form is a plain right-aligned 11-DIP tertiary number, which is what the landed
// Curated shortcut rows already drew; Classic's `InfoBadge.Count` was the outlier.
//
// One owner, so a badge can never diverge per mode again: every sidebar row that shows a count goes through Badge().
static class SidebarCounts
{
    /// <summary>The pending plate's box (20×12) — deliberately NARROWER and SHORTER than the retired 22×16 pill, so the
    /// placeholder reads as "a number is coming" rather than as a badge in its own right.</summary>
    public const float PlateW = 20f;
    public const float PlateH = 12f;

    /// <summary>A count, or the shared pending plate when <paramref name="count"/> is null (the stats cell has not
    /// resolved yet). Shrink=0 so the row's Grow=1 label pushes it to the trailing edge and it never compresses.</summary>
    public static Element Badge(int? count)
        => count is { } n ? Number(n) : Pending();

    /// <summary>The resolved number. 11f tertiary, one line, never wrapped.</summary>
    public static Element Number(int count) => new TextEl(count.ToString())
    {
        Size = 11f,
        Color = Tok.TextTertiary,
        MaxLines = 1,
        Shrink = 0f,
    };

    /// <summary>The one shimmer plate every pending count shows.</summary>
    public static Element Pending() => new BoxEl
    {
        Width = PlateW, Height = PlateH, Shrink = 0f,
        Corners = Radii.ControlAll,
        Fill = Tok.FillSubtleSecondary,
    };
}
