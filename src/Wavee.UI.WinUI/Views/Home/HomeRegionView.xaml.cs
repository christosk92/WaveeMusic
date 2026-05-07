using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.ViewModels.Home;
using Windows.UI;

namespace Wavee.UI.WinUI.Views.Home;

/// <summary>
/// Renders one <see cref="HomeRegion"/> as a full-bleed band with a caps
/// eyebrow + 28 px header + nested ItemsRepeater of its sections. The mica
/// tint colour is derived from <see cref="HomeRegion.AccentColor"/> at two
/// alphas (α=0x10 weak, α=0x18 strong) and assigned imperatively to the
/// gradient stops because XAML lacks a built-in alpha-multiply pipeline.
/// </summary>
public sealed partial class HomeRegionView : UserControl
{
    public HomeRegionView()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty RegionProperty = DependencyProperty.Register(
        nameof(Region), typeof(HomeRegion), typeof(HomeRegionView),
        new PropertyMetadata(null, (d, _) => ((HomeRegionView)d).OnRegionChanged()));

    public HomeRegion? Region
    {
        get => (HomeRegion?)GetValue(RegionProperty);
        set => SetValue(RegionProperty, value);
    }

    /// <summary>
    /// Template selector for the inner sections. Set from <c>HomePage.xaml</c>
    /// so the existing <c>HomeSectionTemplateSelector</c> instance (with its
    /// Generic / Baseline / Shorts templates) is reused — no fork.
    /// </summary>
    public static readonly DependencyProperty SectionTemplateSelectorProperty = DependencyProperty.Register(
        nameof(SectionTemplateSelector), typeof(DataTemplateSelector), typeof(HomeRegionView),
        new PropertyMetadata(null, (d, _) => ((HomeRegionView)d).OnSectionTemplateSelectorChanged()));

    public DataTemplateSelector? SectionTemplateSelector
    {
        get => (DataTemplateSelector?)GetValue(SectionTemplateSelectorProperty);
        set => SetValue(SectionTemplateSelectorProperty, value);
    }

    private void OnRegionChanged()
    {
        var region = Region;
        if (region is null)
        {
            EyebrowText.Text = string.Empty;
            HeaderText.Text = string.Empty;
            SectionsRepeater.ItemsSource = null;
            ApplyTint(default);
            EyebrowText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            return;
        }

        EyebrowText.Text = region.Eyebrow;
        HeaderText.Text = region.Header;
        SectionsRepeater.ItemsSource = region.Sections;
        ApplyTint(region.AccentColor);
        EyebrowText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(region.AccentColor);
    }

    private void OnSectionTemplateSelectorChanged()
    {
        SectionsRepeater.ItemTemplate = SectionTemplateSelector;
    }

    private void ApplyTint(Color accent)
    {
        // Two-stop horizontal mica fade matching the prototype:
        //   transparent → weak → strong → transparent
        // Weak ≈ α 0x10 (16/256), strong ≈ α 0x18 (24/256). Subtle enough to
        // read as "wash" rather than "tinted slab", strong enough to identify
        // the region at a glance.
        TintStop0.Color = Color.FromArgb(0, accent.R, accent.G, accent.B);
        TintStop1.Color = Color.FromArgb(0x10, accent.R, accent.G, accent.B);
        TintStop2.Color = Color.FromArgb(0x18, accent.R, accent.G, accent.B);
        TintStop3.Color = Color.FromArgb(0, accent.R, accent.G, accent.B);
    }
}
