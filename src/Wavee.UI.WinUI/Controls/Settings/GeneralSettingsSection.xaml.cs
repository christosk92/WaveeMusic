using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Controls.Settings;

[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial class GeneralSettingsSection : UserControl, ISettingsSearchFilter
{
    public SettingsViewModel ViewModel { get; }

    public GeneralSettingsSection(SettingsViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
    }

    public void ApplySearchFilter(string? groupKey)
        => SettingsGroupFilter.Apply(SettingsGroupsRoot, groupKey);

    // ── Display scale: preset tile + pip click routing ─────────────────
    //
    // The Compact / Default / Spacious tiles and the 7 dots in the pip
    // strip all carry their target ZoomLevelIndex in Tag. A single shared
    // handler reads Tag and mutates ViewModel.ZoomLevelIndex — the
    // OnZoomLevelIndexChanged → ZoomChanged → ShellPage.ApplyZoom pipeline
    // does the rest, so click → tile / pip visual update / actual zoom /
    // ZoomHud all stay in sync without per-element wiring.

    private void OnZoomPresetClick(object sender, RoutedEventArgs e)
        => SetZoomIndexFromTag(sender);

    private void OnZoomPipClick(object sender, RoutedEventArgs e)
        => SetZoomIndexFromTag(sender);

    private void SetZoomIndexFromTag(object sender)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.Tag is not string tagText) return;
        if (!int.TryParse(tagText, out var index)) return;
        if (index < 0 || index >= SettingsViewModel.ZoomStopCount) return;
        ViewModel.ZoomLevelIndex = index;
    }

    // ── x:Bind selection helpers ────────────────────────────────────────
    //
    // Static so the generated x:Bind code can call them without an
    // instance lookup. Each one takes (this pip / tile's anchor index,
    // current active ZoomLevelIndex) and returns the visual property to
    // apply. WinUI's x:Bind picks up function-call updates whenever any
    // argument changes, so a single ZoomLevelIndex PropertyChanged
    // refreshes every pip / tile binding at once.

    public static Brush PresetTileBorderBrush(int presetIndex, int activeIndex)
        => presetIndex == activeIndex
            ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
            : (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];

    public static Thickness PresetTileBorderThickness(int presetIndex, int activeIndex)
        => presetIndex == activeIndex ? new Thickness(2) : new Thickness(1);

    public static Brush PipFill(int pipIndex, int activeIndex)
        => pipIndex == activeIndex
            ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
            : (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];

    public static double PipSize(int pipIndex, int activeIndex)
        => pipIndex == activeIndex ? 14.0 : 10.0;
}
