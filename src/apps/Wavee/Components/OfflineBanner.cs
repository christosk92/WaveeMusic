using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>A persistent OFFLINE banner — degrade to cached content rather than blanking the surface.</summary>
public static class OfflineBanner
{
    public static Element Build(string? message = null, Action? onRetry = null)
    {
        var kids = new List<Element>
        {
            Icon(Icons.InfoBarBackgroundCircle, 16f, Tok.SystemFillCaution),
            WaveeType.TrackMeta(message ?? Loc.Get(Strings.Common.Offline)),
            new BoxEl { Grow = 1 },
        };
        if (onRetry is not null) kids.Add(Button.Standard(Loc.Get(Strings.Common.Retry), onRetry));
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
            Padding = new Edges4(Spacing.L, Spacing.S, Spacing.L, Spacing.S),
            Fill = Tok.SystemFillCautionBackground, Corners = CornerRadius4.All(Radii.Control),
            Children = kids.ToArray(),
        };
    }
}
