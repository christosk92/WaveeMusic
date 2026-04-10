using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.Controls;

public sealed record PlayActionResult(PlayAction Action, bool AskEveryTime, string? TapMode);

public enum PlayAction
{
    PlayAndClear,
    PlayNext,
    PlayLater,
    Cancelled
}

/// <summary>
/// Single combined dialog for play behavior preferences.
/// Shows tap mode (first-time only) + play action + "ask me every time" toggle.
/// </summary>
public static class PlayActionDialog
{
    /// <summary>
    /// Shows the play action dialog. If <paramref name="isFirstTime"/> is true,
    /// also includes the tap mode selection (single vs double tap).
    /// </summary>
    public static async Task<PlayActionResult> ShowAsync(
        XamlRoot xamlRoot,
        bool isFirstTime = false,
        bool askEveryTime = true)
    {
        var panel = new StackPanel { Spacing = 8, MinWidth = 300 };

        // ── Tap mode section (first-time only) ──
        RadioButton? singleTapRadio = null;
        RadioButton? doubleTapRadio = null;

        if (isFirstTime)
        {
            panel.Children.Add(new TextBlock
            {
                Text = AppLocalization.GetString("PlayAction_TapModeQuestion"),
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"]
            });

            singleTapRadio = new RadioButton { Content = AppLocalization.GetString("PlayAction_SingleTap"), GroupName = "TapMode" };
            doubleTapRadio = new RadioButton { Content = AppLocalization.GetString("PlayAction_DoubleTap"), GroupName = "TapMode", IsChecked = true };
            panel.Children.Add(singleTapRadio);
            panel.Children.Add(doubleTapRadio);

            panel.Children.Add(new Border { Height = 1, Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"], Margin = new Thickness(0, 4, 0, 4) });
        }

        // ── Play action section ──
        panel.Children.Add(new TextBlock
        {
            Text = AppLocalization.GetString("PlayAction_KeepQueuedQuestion"),
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap
        });

        var playAndClearBtn = CreateActionButton("\uE768", AppLocalization.GetString("PlayAction_PlayAndClear"), true);
        var playNextBtn = CreateActionButton("\uE8DE", AppLocalization.GetString("PlayAction_PlayNext"), false);
        var playLaterBtn = CreateActionButton("\uE8DE", AppLocalization.GetString("PlayAction_PlayLater"), false);
        var cancelBtn = CreateActionButton("\uE711", AppLocalization.GetString("PlayAction_Cancel"), false);

        PlayAction selectedAction = PlayAction.Cancelled;
        ContentDialog? dialogRef = null;

        playAndClearBtn.Click += (_, _) => { selectedAction = PlayAction.PlayAndClear; dialogRef?.Hide(); };
        playNextBtn.Click += (_, _) => { selectedAction = PlayAction.PlayNext; dialogRef?.Hide(); };
        playLaterBtn.Click += (_, _) => { selectedAction = PlayAction.PlayLater; dialogRef?.Hide(); };
        cancelBtn.Click += (_, _) => { selectedAction = PlayAction.Cancelled; dialogRef?.Hide(); };

        panel.Children.Add(playAndClearBtn);
        panel.Children.Add(playNextBtn);
        panel.Children.Add(playLaterBtn);
        panel.Children.Add(cancelBtn);

        // ── Ask every time toggle ──
        var askToggle = new ToggleSwitch
        {
            Header = AppLocalization.GetString("PlayAction_AskEveryTime"),
            IsOn = askEveryTime,
            Margin = new Thickness(0, 8, 0, 0)
        };
        panel.Children.Add(askToggle);

        if (isFirstTime)
        {
            panel.Children.Add(new TextBlock
            {
                Text = AppLocalization.GetString("PlayAction_SettingsHint"),
                FontSize = 12,
                Opacity = 0.6
            });
        }

        // ── Show dialog ──
        var dialog = new ContentDialog
        {
            Title = AppLocalization.GetString(isFirstTime ? "PlayAction_PlaybackPreferences" : "PlayAction_PlayAction"),
            Content = panel,
            XamlRoot = xamlRoot
        };
        dialogRef = dialog;

        await dialog.ShowAsync();

        string? tapMode = null;
        if (isFirstTime)
            tapMode = singleTapRadio?.IsChecked == true ? "SingleTap" : "DoubleTap";

        return new PlayActionResult(selectedAction, askToggle.IsOn, tapMode);
    }

    private static Button CreateActionButton(string glyph, string label, bool isAccent)
    {
        var icon = new FontIcon { Glyph = glyph, FontSize = 16 };
        var text = new TextBlock { Text = label, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        stack.Children.Add(icon);
        stack.Children.Add(text);

        var btn = new Button
        {
            Content = stack,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(16, 10, 16, 10)
        };

        if (isAccent)
            btn.Style = (Style)Application.Current.Resources["AccentButtonStyle"];

        return btn;
    }
}
