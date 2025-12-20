using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Wavee.WinUI.Helpers;
using Wavee.WinUI.Models.Dto;
using Wavee.WinUI.Services;

namespace Wavee.WinUI.ViewModels;

/// <summary>
/// Main ViewModel for the Home view
/// </summary>
public partial class HomeViewModel : ObservableObject
{
    private readonly ILogger<HomeViewModel> _logger;
    private readonly MockSpotifyService _spotifyService;

    [ObservableProperty]
    public partial string? Greeting { get; set; }

    [ObservableProperty]
    public partial ObservableCollection<SectionViewModelBase> Sections { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    public partial string? ErrorTitle { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial string? ErrorDetails { get; set; }

    /// <summary>
    /// Dummy collection for shimmer loading sections (3 sections with 6 cards each)
    /// </summary>
    public int[][] ShimmerItems { get; } = Enumerable.Range(0, 3)
        .Select(_ => Enumerable.Range(0, 12).ToArray())
        .ToArray();

    public HomeViewModel(ILogger<HomeViewModel> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _logger.LogDebug("HomeViewModel constructor starting");

        _spotifyService = new MockSpotifyService();
        Sections = new();

        _logger.LogInformation("HomeViewModel constructed, starting data load");
        LoadDataAsync().SafeFireAndForget();
    }

    /// <summary>
    /// Loads the home feed data
    /// </summary>
    [RelayCommand]
    private async Task LoadDataAsync()
    {
        _logger.LogInformation("LoadDataAsync starting");

        // Reset error state
        HasError = false;
        ErrorTitle = null;
        ErrorMessage = null;
        ErrorDetails = null;
        IsLoading = true;

        try
        {
            _logger.LogDebug("Fetching home data from MockSpotifyService");
            var homeResponse = await _spotifyService.GetHomeDataAsync();
            _logger.LogDebug("Home data fetched successfully");

            if (homeResponse != null)
            {
                // Extract greeting from greeting.transformedLabel
                Greeting = homeResponse.Greeting?.TransformedLabel ?? "Good evening";
                _logger.LogDebug("Greeting extracted: {Greeting}", Greeting);

                // Clear existing sections
                Sections.Clear();

                // Convert DTOs to ViewModels from sectionContainer.sections.items
                if (homeResponse.SectionContainer?.Sections?.Items != null)
                {
                    _logger.LogDebug("Processing {SectionCount} sections from response",
                        homeResponse.SectionContainer.Sections.Items.Length);
                    // Collect baseline sections for aggregation
                    var baselineSections = new System.Collections.Generic.List<(string title, HomeSectionDto section)>();

                    foreach (var section in homeResponse.SectionContainer.Sections.Items)
                    {
                        SectionViewModelBase? vm = null;

                        // Check if this is a HomeFeedBaselineSectionData - collect for aggregation
                        if (section.Data is HomeFeedBaselineSectionDataDto baselineData)
                        {
                            var title = baselineData.Title?.TransformedLabel ?? string.Empty;
                            if (!string.IsNullOrEmpty(title) && section.SectionItems?.Items != null)
                            {
                                baselineSections.Add((title, section));
                            }
                            continue; // Skip adding to Sections directly
                        }

                        // Check if this is a deferred section type (HomeRecentlyPlayedSectionData)
                        if (section.Data is HomeRecentlyPlayedSectionDataDto recentlyPlayedData)
                        {
                            // Create a deferred section that will load independently
                            var deferredVm = new DeferredSectionViewModel(
                                recentlyPlayedData.Title?.TransformedLabel ?? "Recently Played"
                            );
                            // Trigger background load (stays in loading state for now as placeholder)
                            deferredVm.LoadDataCommand.ExecuteAsync(null).SafeFireAndForget();
                            vm = deferredVm;
                        }
                        else
                        {
                            // Regular static section loaded from DTO
                            vm = new HomeSectionViewModel(section);
                        }

                        // Skip sections without titles, unless they're grid layout sections (like shorts)
                        if (vm != null && (!string.IsNullOrEmpty(vm.Title) || vm.IsGridLayout))
                        {
                            Sections.Add(vm);
                        }
                    }

                    // Create aggregated uniform grid section from collected baseline sections
                    _logger.LogDebug("Creating aggregated section from {BaselineCount} baseline sections",
                        baselineSections.Count);
                    if (baselineSections.Count > 0)
                    {
                        var aggregatedSection = new HomeSectionViewModel
                        {
                            Title = string.Empty, // No overall title for the grid
                            IsUniformGrid = true
                        };

                        foreach (var (title, section) in baselineSections)
                        {
                            // Get the first (and typically only) item from the section
                            if (section.SectionItems?.Items != null && section.SectionItems.Items.Length > 0)
                            {
                                var firstItem = section.SectionItems.Items[0];
                                var itemVm = ContentItemViewModel.FromWrapper(firstItem.Content);

                                if (itemVm != null)
                                {
                                    // Create a card that combines section title + item
                                    var card = new BaselineSectionCardViewModel(title, itemVm);
                                    aggregatedSection.Items.Add(card);
                                }
                            }
                        }

                        // Add the aggregated section at the end
                        if (aggregatedSection.Items.Count > 0)
                        {
                            Sections.Add(aggregatedSection);
                            _logger.LogDebug("Aggregated section added with {ItemCount} items",
                                aggregatedSection.Items.Count);
                        }
                    }
                }

                _logger.LogInformation("Home data loaded successfully: {SectionCount} sections, Greeting: {Greeting}",
                    Sections.Count,
                    Greeting);
            }
            else
            {
                // Service returned null
                _logger.LogWarning("Service returned null response");
                HasError = true;
                ErrorTitle = "Unable to Load Home Feed";
                ErrorMessage = "The home feed data could not be loaded. This might be due to an invalid data format or missing file.";
                ErrorDetails = "The service returned null. Check that Home.json exists and is properly formatted.";
            }
        }
        catch (System.Text.Json.JsonException jsonEx)
        {
            // JSON deserialization error
            _logger.LogError(jsonEx, "JSON deserialization error while loading home data");
            HasError = true;
            ErrorTitle = "Data Format Error";
            ErrorMessage = "The home feed data couldn't be parsed. This might be due to an unrecognized content type or invalid JSON structure.";
            ErrorDetails = $"JSON Error: {jsonEx.Message}\n\nStack Trace:\n{jsonEx.StackTrace}";
        }
        catch (Exception ex)
        {
            // General error
            _logger.LogError(ex, "Unexpected error loading home data: {ErrorType}", ex.GetType().Name);
            HasError = true;
            ErrorTitle = "Unexpected Error";
            ErrorMessage = "An unexpected error occurred while loading the home feed. Please try again.";
            ErrorDetails = $"Error: {ex.Message}\n\nType: {ex.GetType().Name}\n\nStack Trace:\n{ex.StackTrace}";
        }
        finally
        {
            IsLoading = false;
            _logger.LogDebug("LoadDataAsync completed, IsLoading set to false");
        }
    }
}
