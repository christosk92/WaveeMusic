using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views.Shared;

/// <summary>
/// Reusable flat-section feed — renders an <see cref="IList{T}"/> of
/// <see cref="HomeSection"/> as a vertical stack of shelves (title +
/// horizontal <c>ShelfScroller</c> of <c>ContentCard</c>s). Used by
/// <c>BrowsePage</c> below the hero band; HomePage's HomeRegionView
/// will migrate to it in a follow-up.
/// </summary>
public sealed partial class SectionShelvesView : UserControl
{
    public SectionShelvesView()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty SectionsProperty = DependencyProperty.Register(
        nameof(Sections), typeof(IList<HomeSection>), typeof(SectionShelvesView),
        new PropertyMetadata(null, (d, e) =>
            ((SectionShelvesView)d).SectionsRepeater.ItemsSource = e.NewValue));

    public IList<HomeSection>? Sections
    {
        get => (IList<HomeSection>?)GetValue(SectionsProperty);
        set => SetValue(SectionsProperty, value);
    }
}
