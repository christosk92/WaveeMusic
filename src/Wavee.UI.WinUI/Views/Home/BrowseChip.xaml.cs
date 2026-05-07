using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels;
using Wavee.UI.WinUI.ViewModels.Home;
using Windows.UI;

namespace Wavee.UI.WinUI.Views.Home;

/// <summary>
/// One chip in the Browse All grid. Resting state is a clean dimmed text
/// label with no background. On pointer-over, the chip's container tints
/// with the item's accent hex (background ~α 0x2E, border ~α 0x73) and the
/// label snaps to full primary foreground — matching the prototype rule
/// "color earns its place at the moment of consideration."
/// </summary>
public sealed partial class BrowseChip : UserControl
{
    public BrowseChip()
    {
        InitializeComponent();
        PointerEntered += OnPointerEntered;
        PointerExited += OnPointerExited;
        PointerCaptureLost += OnPointerExited;
        Tapped += OnTapped;
    }

    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
        nameof(Item), typeof(BrowseAllItem), typeof(BrowseChip),
        new PropertyMetadata(null, (d, _) => ((BrowseChip)d).OnItemChanged()));

    public BrowseAllItem? Item
    {
        get => (BrowseAllItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    private void OnItemChanged()
    {
        LabelText.Text = Item?.Label ?? string.Empty;
        // Reset hover state when the item changes (recycled by ItemsRepeater).
        ApplyResting();
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        ApplyHover();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        ApplyResting();
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        if (Item is null || string.IsNullOrEmpty(Item.Uri)) return;
        // Same navigation path the rest of Home uses for spotify: URIs.
        // browseAll items resolve to spotify:page:... and spotify:xlink:...
        // — let the central handler dispatch.
        HomeViewModel.NavigateToItem(new HomeSectionItem { Uri = Item.Uri }, NavigationHelpers.IsCtrlPressed());
    }

    private void ApplyHover()
    {
        if (Item is null) return;
        if (TintColorHelper.TryParseHex(Item.AccentHex, out var c))
        {
            ChipRoot.Background = new SolidColorBrush(Color.FromArgb(0x2E, c.R, c.G, c.B));
            ChipRoot.BorderBrush = new SolidColorBrush(Color.FromArgb(0x73, c.R, c.G, c.B));
        }
        else
        {
            ChipRoot.Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
            ChipRoot.BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorSecondaryBrush"];
        }
        LabelText.Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
    }

    private void ApplyResting()
    {
        ChipRoot.Background = new SolidColorBrush(Colors.Transparent);
        ChipRoot.BorderBrush = new SolidColorBrush(Colors.Transparent);
        LabelText.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
    }
}
