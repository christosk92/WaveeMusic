using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Data.Models;

namespace Wavee.UI.WinUI.Controls;

public enum CloseTabsDialogChoice
{
    Save,
    Discard,
    Cancel,
}

public sealed record CloseTabsDialogResult(CloseTabsDialogChoice Choice, bool AlwaysAsk);

public static class CloseTabsDialog
{
    public static async Task<CloseTabsDialogResult> ShowAsync(XamlRoot xamlRoot, bool alwaysAsk)
    {
        var askToggle = new ToggleSwitch
        {
            Header = "Always ask me this",
            IsOn = alwaysAsk,
            Margin = new Thickness(0, 12, 0, 0)
        };

        var content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = "Do you want to save your open tabs for the next launch, discard them, or cancel closing?",
                    TextWrapping = TextWrapping.Wrap
                },
                askToggle
            }
        };

        var dialog = new ContentDialog
        {
            Title = "Close Wavee",
            Content = content,
            PrimaryButtonText = "Save Tabs",
            SecondaryButtonText = "Discard Tabs",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot
        };

        var result = await dialog.ShowAsync();
        var choice = result switch
        {
            ContentDialogResult.Primary => CloseTabsDialogChoice.Save,
            ContentDialogResult.Secondary => CloseTabsDialogChoice.Discard,
            _ => CloseTabsDialogChoice.Cancel
        };

        return new CloseTabsDialogResult(choice, askToggle.IsOn);
    }
}
