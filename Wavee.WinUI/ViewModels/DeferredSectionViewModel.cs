using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Wavee.WinUI.ViewModels;

/// <summary>
/// ViewModel for a section that loads its data independently with its own loading/error states
/// </summary>
public partial class DeferredSectionViewModel : SectionViewModelBase, IDisposable
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    /// <summary>
    /// Dummy collection for shimmer loading cards (6 cards for loading state)
    /// </summary>
    public int[] ShimmerCards { get; } = Enumerable.Range(0, 6).ToArray();

    public DeferredSectionViewModel(string title, string? subtitle = null)
    {
        Title = title;
        Subtitle = subtitle;
        IsLoading = true; // Start in loading state
    }

    /// <summary>
    /// Loads the section data asynchronously
    /// </summary>
    [RelayCommand]
    private async Task LoadDataAsync()
    {
        await LoadDataAsync(_cancellationTokenSource.Token);
    }

    /// <summary>
    /// Internal method that loads the section data with cancellation support
    /// </summary>
    private async Task LoadDataAsync(CancellationToken cancellationToken)
    {
        IsLoading = true;
        HasError = false;
        ErrorMessage = null;

        try
        {
            // TODO: Implement actual data fetching logic
            // For now, this stays in loading state indefinitely as a placeholder
            await Task.Delay(100, cancellationToken); // Placeholder

            // Example of what will be implemented later:
            // var data = await _dataService.FetchRecentlyPlayedAsync(cancellationToken);
            // Items.Clear();
            // foreach (var item in data)
            // {
            //     Items.Add(item);
            // }
            // IsLoading = false;
        }
        catch (OperationCanceledException)
        {
            // Task was cancelled - this is expected during disposal
            IsLoading = false;
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = ex.Message;
            IsLoading = false;
            System.Diagnostics.Debug.WriteLine($"Error loading deferred section '{Title}': {ex.Message}");
        }
    }

    public void Dispose()
    {
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
    }
}
