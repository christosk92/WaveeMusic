using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Media;
using Wavee.UI.WinUI.Helpers;
using Windows.UI;

namespace Wavee.UI.WinUI.ViewModels.Home;

/// <summary>
/// One Browse All chip — a top-level category like "Pop" or "Made For You".
/// The label and accent come straight from the Pathfinder <c>browseAll</c>
/// response (<c>title.transformedLabel</c> + <c>backgroundColor.hex</c>);
/// the URI is what the chip click navigates to.
/// </summary>
public sealed class BrowseAllItem
{
    public string Label { get; init; } = "";
    public string AccentHex { get; init; } = "";
    public string Uri { get; init; } = "";

    /// <summary>
    /// Pre-computed accent brush so XAML can bind without a converter. Falls
    /// back to a neutral grey when the API gave no usable hex.
    /// </summary>
    public Brush AccentBrush
    {
        get
        {
            if (TintColorHelper.TryParseHex(AccentHex, out var c))
                return new SolidColorBrush(c);
            return new SolidColorBrush(Color.FromArgb(255, 0x55, 0x55, 0x55));
        }
    }
}

/// <summary>One group in the Browse All taxonomy (TOP / FOR YOU / GENRES / …).</summary>
public sealed class BrowseAllGroup
{
    public string Eyebrow { get; init; } = "";
    public ObservableCollection<BrowseAllItem> Items { get; } = new();
}
