using Microsoft.UI.Xaml.Controls;

namespace Wavee.UI.WinUI.Views.Shared;

/// <summary>
/// Skeleton placeholder for HomePage's content tree. Mirrors the real layout
/// 1:1 — greeting band, 480 px hero band (with the same wide/stacked VSM as
/// the real hero), and three region bands carrying generic shelves and one
/// baseline grid — so the shimmer → content crossfade has minimal reflow.
/// Released after first crossfade via ShimmerContainer.Content = null.
/// </summary>
public sealed partial class HomePageShimmer : UserControl
{
    public HomePageShimmer()
    {
        InitializeComponent();
    }
}
