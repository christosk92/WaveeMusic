using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.WinUI.ViewModels;

namespace Wavee.WinUI.Selectors;

/// <summary>
/// Selects the appropriate DataTemplate based on section ViewModel type
/// </summary>
public partial class SectionDataTemplateSelector : DataTemplateSelector
{
    public DataTemplate? StaticSectionTemplate { get; set; }
    public DataTemplate? DeferredSectionTemplate { get; set; }
    public DataTemplate? GridSectionTemplate { get; set; }
    public DataTemplate? UniformGridSectionTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item)
    {
        return item switch
        {
            DeferredSectionViewModel => DeferredSectionTemplate,
            HomeSectionViewModel vm when vm.IsUniformGrid => UniformGridSectionTemplate,
            HomeSectionViewModel vm when vm.IsGridLayout => GridSectionTemplate,
            HomeSectionViewModel => StaticSectionTemplate,
            _ => StaticSectionTemplate
        };
    }

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
    {
        return SelectTemplateCore(item);
    }
}
