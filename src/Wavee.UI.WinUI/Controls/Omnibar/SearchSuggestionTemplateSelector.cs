using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.Contracts;

namespace Wavee.UI.WinUI.Controls.Omnibar;

public sealed partial class SearchSuggestionTemplateSelector : DataTemplateSelector
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