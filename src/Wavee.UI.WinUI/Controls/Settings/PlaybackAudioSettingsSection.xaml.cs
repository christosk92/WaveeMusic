using System;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Controls.Settings;

public sealed partial class PlaybackAudioSettingsSection : UserControl, ISettingsSearchFilter
{
    private readonly PlaybackSettingsSection _playback;
    private readonly AudioSettingsSection _audio;

    public SettingsViewModel ViewModel { get; }

    public PlaybackAudioSettingsSection(SettingsViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();

        _playback = new PlaybackSettingsSection(viewModel);
        _audio = new AudioSettingsSection(viewModel);
        Host.Children.Add(_playback);
        Host.Children.Add(_audio);
    }

    public void ApplySearchFilter(string? groupKey)
    {
        _playback.ApplySearchFilter(groupKey);
        _audio.ApplySearchFilter(groupKey);
    }
}
