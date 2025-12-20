using Wavee.WinUI.Models.Dto;

namespace Wavee.WinUI.ViewModels;

/// <summary>
/// ViewModel for a static home feed section loaded from DTO
/// </summary>
public partial class HomeSectionViewModel : SectionViewModelBase
{
    public HomeSectionViewModel()
    {
    }

    public HomeSectionViewModel(HomeSectionDto dto)
    {
        // Extract title and subtitle from data.title/subtitle
        if (dto.Data is HomeGenericSectionDataDto genericData)
        {
            Title = genericData.Title?.TransformedLabel ?? string.Empty;
            Subtitle = genericData.Subtitle?.TransformedLabel;
        }
        else if (dto.Data is HomeRecentlyPlayedSectionDataDto recentlyPlayedData)
        {
            Title = recentlyPlayedData.Title?.TransformedLabel ?? string.Empty;
        }
        else if (dto.Data is HomeFeedBaselineSectionDataDto baselineData)
        {
            Title = baselineData.Title?.TransformedLabel ?? string.Empty;
        }
        else if (dto.Data is HomeShortsSectionDataDto)
        {
            // Shorts section has no title/subtitle - uses grid layout with horizontal cards
            Title = string.Empty;
            IsGridLayout = true;
        }

        // Convert content wrappers to ViewModels
        if (dto.SectionItems?.Items != null)
        {
            foreach (var item in dto.SectionItems.Items)
            {
                var viewModel = ContentItemViewModel.FromWrapper(item.Content);
                if (viewModel != null)
                {
                    Items.Add(viewModel);
                }
            }
        }
    }
}
