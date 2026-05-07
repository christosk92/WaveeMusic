using Microsoft.UI.Xaml.Controls;

namespace Wavee.UI.WinUI.Views.Shared;

/// <summary>
/// Skeleton placeholder for <see cref="HeroBandPanel"/>. Renders a 420 px
/// shimmer rectangle plus a faint cluster of text-line placeholders bottom-
/// left so the shimmer reads as a hero band rather than a blank rail.
/// Used by <c>BrowsePage</c> while the <c>browsePage</c> fetch is in flight;
/// HomePage will adopt this in Phase 11g.
/// </summary>
public sealed partial class HeroBandShimmer : UserControl
{
    public HeroBandShimmer()
    {
        InitializeComponent();
    }
}
