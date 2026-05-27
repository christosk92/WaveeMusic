using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Services;

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
            Header = AppLocalization.GetString("CloseTabs_AlwaysAsk"),
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
                    Text = AppLocalization.GetString("CloseTabs_Question"),
                    TextWrapping = TextWrapping.Wrap
                },
                askToggle
            }
        };

        var dialog = new ContentDialog
        {
            Title = AppLocalization.GetString("CloseTabs_Title"),
            Content = content,
            PrimaryButtonText = AppLocalization.GetString("CloseTabs_SaveTabs"),
            SecondaryButtonText = AppLocalization.GetString("CloseTabs_DiscardTabs"),
            CloseButtonText = AppLocalization.GetString("Dialog_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
            RequestedTheme = ResolveTheme(xamlRoot),
            Style = Application.Current.Resources.TryGetValue("DefaultContentDialogStyle", out var style)
                    && style is Style contentDialogStyle
                ? contentDialogStyle
                : null,
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

    private static ElementTheme ResolveTheme(XamlRoot xamlRoot)
        => xamlRoot.Content is FrameworkElement root
            ? root.ActualTheme
            : ElementTheme.Default;
}
