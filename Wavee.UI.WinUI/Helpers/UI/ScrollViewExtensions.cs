using Microsoft.UI.Xaml.Controls;

namespace Wavee.UI.WinUI.Helpers.UI;

public static class ScrollViewExtensions
{
    private static readonly ScrollingScrollOptions ImmediateScrollOptions =
        new(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore);

    public static void ScrollToImmediate(this ScrollView scrollView, double horizontalOffset, double verticalOffset)
        => scrollView.ScrollTo(horizontalOffset, verticalOffset, ImmediateScrollOptions);
}
