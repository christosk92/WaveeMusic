using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Controls.Omnibar;

public sealed class SearchSuggestionTemplateSelector : DataTemplateSelector
{
    public DataTemplate? TextQueryTemplate { get; set; }
    public DataTemplate? EntityTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item)
    {
        if (item is SearchSuggestionItem { Type: SearchSuggestionType.TextQuery })
            return TextQueryTemplate!;
        return EntityTemplate!;
    }

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
    {
        return SelectTemplateCore(item);
    }
}
