using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Models.PodcastBrowse;

namespace Wavee.UI.WinUI.Controls.PodcastBrowse;

/// <summary>
/// Routes one <see cref="PodcastBrowseSection"/> to the right DataTemplate
/// based on its <see cref="PodcastBrowseSection.LayoutKind"/>. The four
/// template slots are filled inline by <c>PodcastBrowsePage.xaml</c>'s
/// section host; only <see cref="ArtworkRailTemplate"/> and
/// <see cref="CtaTemplate"/> have distinct visuals today — the ranked / plain
/// list slots fall back to the artwork rail until Spotify ships responses
/// that warrant their own treatment.
/// </summary>
public sealed class PodcastBrowseSectionTemplateSelector : DataTemplateSelector
{
    public DataTemplate? ArtworkRailTemplate { get; set; }
    public DataTemplate? CtaTemplate { get; set; }
    public DataTemplate? RankedListTemplate { get; set; }
    public DataTemplate? PlainListTemplate { get; set; }
    /// <summary>Multi-row UniformGridLayout used for single-section
    /// drilled category pages with pagination ("Show more" affordance).</summary>
    public DataTemplate? GridTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item)
    {
        if (item is not PodcastBrowseSection section)
            return ArtworkRailTemplate;

        return section.LayoutKind switch
        {
            PodcastBrowseSectionLayoutKind.Cta        => CtaTemplate ?? ArtworkRailTemplate,
            PodcastBrowseSectionLayoutKind.RankedList => RankedListTemplate ?? ArtworkRailTemplate,
            PodcastBrowseSectionLayoutKind.PlainList  => PlainListTemplate ?? ArtworkRailTemplate,
            PodcastBrowseSectionLayoutKind.Grid       => GridTemplate ?? ArtworkRailTemplate,
            _                                         => ArtworkRailTemplate,
        };
    }

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
        => SelectTemplateCore(item);
}
