using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Controls.Omnibar;

public sealed class SearchSuggestionTemplateSelector : DataTemplateSelector
{
    public DataTemplate? TextQueryTemplate { get; set; }
    public DataTemplate? EntityTemplate { get; set; }
    public DataTemplate? SectionHeaderTemplate { get; set; }
    public DataTemplate? SettingTemplate { get; set; }
    public DataTemplate? ShimmerTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item)
    {
        if (item is not SearchSuggestionItem entry)
            return EntityTemplate!;

        return entry.Type switch
        {
            SearchSuggestionType.SectionHeader => SectionHeaderTemplate ?? EntityTemplate!,
            SearchSuggestionType.Setting       => SettingTemplate ?? EntityTemplate!,
            SearchSuggestionType.Shimmer       => ShimmerTemplate ?? EntityTemplate!,
            SearchSuggestionType.TextQuery     => TextQueryTemplate!,
            _ => EntityTemplate!,
        };
    }

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
    {
        return SelectTemplateCore(item);
    }
}

/// <summary>
/// Drives <see cref="VariableSizedWrapGrid.ColumnSpan"/> + hit-testing for items in the
/// omnibar's flat suggestions list. Section header rows span the full grid width and
/// are non-interactive; everything else takes a single cell.
/// </summary>
public sealed class SearchSuggestionContainerStyleSelector : StyleSelector
{
    public Style? DefaultStyle { get; set; }
    public Style? SectionHeaderStyle { get; set; }
    public Style? TextQueryStyle { get; set; }

    protected override Style SelectStyleCore(object item, DependencyObject container)
    {
        if (item is SearchSuggestionItem entry)
        {
            return entry.Type switch
            {
                SearchSuggestionType.SectionHeader => SectionHeaderStyle ?? DefaultStyle!,
                SearchSuggestionType.TextQuery     => TextQueryStyle ?? DefaultStyle!,
                _ => DefaultStyle!,
            };
        }
        return DefaultStyle!;
    }
}
