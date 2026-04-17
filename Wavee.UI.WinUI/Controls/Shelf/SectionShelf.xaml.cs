using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wavee.UI.WinUI.Controls.Shelf;

/// <summary>
/// Thin wrapper around <see cref="ShelfScroller"/> that adds the standard shelf
/// header (title, optional subtitle, optional "Show all" link). Use directly for
/// the common shelf shape; drop to <see cref="ShelfScroller"/> when the header
/// needs bespoke markup (e.g. a location picker or view-mode toggle).
/// </summary>
public sealed partial class SectionShelf : UserControl
{
    public SectionShelf()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SectionShelf),
            new PropertyMetadata(string.Empty));
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(SectionShelf),
            new PropertyMetadata(null));
    public string? Subtitle
    {
        get => (string?)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public static readonly DependencyProperty ShowAllTextProperty =
        DependencyProperty.Register(nameof(ShowAllText), typeof(string), typeof(SectionShelf),
            new PropertyMetadata("Show all >"));
    public string ShowAllText
    {
        get => (string)GetValue(ShowAllTextProperty);
        set => SetValue(ShowAllTextProperty, value);
    }

    public static readonly DependencyProperty ShowAllCommandProperty =
        DependencyProperty.Register(nameof(ShowAllCommand), typeof(ICommand), typeof(SectionShelf),
            new PropertyMetadata(null));
    public ICommand? ShowAllCommand
    {
        get => (ICommand?)GetValue(ShowAllCommandProperty);
        set => SetValue(ShowAllCommandProperty, value);
    }

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(object), typeof(SectionShelf),
            new PropertyMetadata(null, (d, e) => ((SectionShelf)d).PART_Shelf.ItemsSource = e.NewValue));
    public object? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public static readonly DependencyProperty ItemTemplateProperty =
        DependencyProperty.Register(nameof(ItemTemplate), typeof(DataTemplate), typeof(SectionShelf),
            new PropertyMetadata(null, (d, e) => ((SectionShelf)d).PART_Shelf.ItemTemplate = e.NewValue as DataTemplate));
    public DataTemplate? ItemTemplate
    {
        get => (DataTemplate?)GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }

    public static readonly DependencyProperty ItemTemplateSelectorProperty =
        DependencyProperty.Register(nameof(ItemTemplateSelector), typeof(DataTemplateSelector), typeof(SectionShelf),
            new PropertyMetadata(null, (d, e) => ((SectionShelf)d).PART_Shelf.ItemTemplateSelector = e.NewValue as DataTemplateSelector));
    public DataTemplateSelector? ItemTemplateSelector
    {
        get => (DataTemplateSelector?)GetValue(ItemTemplateSelectorProperty);
        set => SetValue(ItemTemplateSelectorProperty, value);
    }

    public static readonly DependencyProperty MinItemWidthProperty =
        DependencyProperty.Register(nameof(MinItemWidth), typeof(double), typeof(SectionShelf),
            new PropertyMetadata(160.0));
    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public static readonly DependencyProperty MaxItemWidthProperty =
        DependencyProperty.Register(nameof(MaxItemWidth), typeof(double), typeof(SectionShelf),
            new PropertyMetadata(200.0));
    public double MaxItemWidth
    {
        get => (double)GetValue(MaxItemWidthProperty);
        set => SetValue(MaxItemWidthProperty, value);
    }

    public static readonly DependencyProperty SpacingProperty =
        DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(SectionShelf),
            new PropertyMetadata(12.0));
    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public static readonly DependencyProperty OverlapCardsProperty =
        DependencyProperty.Register(nameof(OverlapCards), typeof(int), typeof(SectionShelf),
            new PropertyMetadata(1));
    public int OverlapCards
    {
        get => (int)GetValue(OverlapCardsProperty);
        set => SetValue(OverlapCardsProperty, value);
    }
}
