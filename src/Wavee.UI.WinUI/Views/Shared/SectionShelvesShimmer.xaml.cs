using Microsoft.UI.Xaml.Controls;

namespace Wavee.UI.WinUI.Views.Shared;

/// <summary>
/// Skeleton placeholder for <see cref="SectionShelvesView"/>. Renders four
/// repeating shelf placeholders (title shimmer + 5 card stacks) at the same
/// dimensions <c>SectionShelvesView</c> uses, so the swap from skeleton to
/// real shelves is layout-stable.
/// </summary>
public sealed partial class SectionShelvesShimmer : UserControl
{
    public SectionShelvesShimmer()
    {
        InitializeComponent();
    }
}
