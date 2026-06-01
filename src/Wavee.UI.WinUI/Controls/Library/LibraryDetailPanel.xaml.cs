using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Data.Enums;

namespace Wavee.UI.WinUI.Controls.Library;

/// <summary>
/// The single inline detail panel shared by Albums, Artists and Podcasts. Hosts a
/// config-driven hero (art whose shape is <see cref="ArtShape"/> — square for
/// albums/shows, circle for artists), a declarative action row
/// (<see cref="PrimaryActions"/>), an optional inline sub-toggle slot
/// (<see cref="SubToggleContent"/>), and a content slot (<see cref="PanelContent"/>)
/// into which the consuming view drops a <c>TrackListView</c> / <c>TrackDataGrid</c>
/// / discography. Replaces the four hand-rolled per-tab detail headers.
/// </summary>
public sealed partial class LibraryDetailPanel : UserControl
{
    private const double ArtSize = 104.0;

    public LibraryDetailPanel()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ApplyArtShape();
            UpdateArtVisibility();
            UpdateKicker();
            UpdateSubtitle();
            UpdateMetadata();
        };
    }

    // ── Dependency properties ──

    public static readonly DependencyProperty ImageUrlProperty =
        DependencyProperty.Register(nameof(ImageUrl), typeof(string), typeof(LibraryDetailPanel),
            new PropertyMetadata(null, (d, _) => ((LibraryDetailPanel)d).UpdateArtVisibility()));

    public static readonly DependencyProperty ArtShapeProperty =
        DependencyProperty.Register(nameof(ArtShape), typeof(LibraryArtShape), typeof(LibraryDetailPanel),
            new PropertyMetadata(LibraryArtShape.Square, (d, _) => ((LibraryDetailPanel)d).ApplyArtShape()));

    public static readonly DependencyProperty PlaceholderGlyphProperty =
        DependencyProperty.Register(nameof(PlaceholderGlyph), typeof(string), typeof(LibraryDetailPanel),
            new PropertyMetadata(""));

    public static readonly DependencyProperty KickerProperty =
        DependencyProperty.Register(nameof(Kicker), typeof(string), typeof(LibraryDetailPanel),
            new PropertyMetadata("", (d, _) => ((LibraryDetailPanel)d).UpdateKicker()));

    public static readonly DependencyProperty TitleTextProperty =
        DependencyProperty.Register(nameof(TitleText), typeof(string), typeof(LibraryDetailPanel),
            new PropertyMetadata(""));

    public static readonly DependencyProperty SubtitleTextProperty =
        DependencyProperty.Register(nameof(SubtitleText), typeof(string), typeof(LibraryDetailPanel),
            new PropertyMetadata("", (d, _) => ((LibraryDetailPanel)d).UpdateSubtitle()));

    public static readonly DependencyProperty SubtitleCommandProperty =
        DependencyProperty.Register(nameof(SubtitleCommand), typeof(ICommand), typeof(LibraryDetailPanel),
            new PropertyMetadata(null, (d, _) => ((LibraryDetailPanel)d).UpdateSubtitle()));

    public static readonly DependencyProperty MetadataTextProperty =
        DependencyProperty.Register(nameof(MetadataText), typeof(string), typeof(LibraryDetailPanel),
            new PropertyMetadata("", (d, _) => ((LibraryDetailPanel)d).UpdateMetadata()));

    public static readonly DependencyProperty PrimaryActionsProperty =
        DependencyProperty.Register(nameof(PrimaryActions), typeof(object), typeof(LibraryDetailPanel),
            new PropertyMetadata(null));

    public static readonly DependencyProperty SubToggleContentProperty =
        DependencyProperty.Register(nameof(SubToggleContent), typeof(object), typeof(LibraryDetailPanel),
            new PropertyMetadata(null));

    public static readonly DependencyProperty PanelContentProperty =
        DependencyProperty.Register(nameof(PanelContent), typeof(object), typeof(LibraryDetailPanel),
            new PropertyMetadata(null));

    public string? ImageUrl { get => (string?)GetValue(ImageUrlProperty); set => SetValue(ImageUrlProperty, value); }
    public LibraryArtShape ArtShape { get => (LibraryArtShape)GetValue(ArtShapeProperty); set => SetValue(ArtShapeProperty, value); }
    public string PlaceholderGlyph { get => (string)GetValue(PlaceholderGlyphProperty); set => SetValue(PlaceholderGlyphProperty, value); }
    public string Kicker { get => (string)GetValue(KickerProperty); set => SetValue(KickerProperty, value); }
    public string TitleText { get => (string)GetValue(TitleTextProperty); set => SetValue(TitleTextProperty, value); }
    public string SubtitleText { get => (string)GetValue(SubtitleTextProperty); set => SetValue(SubtitleTextProperty, value); }
    public ICommand? SubtitleCommand { get => (ICommand?)GetValue(SubtitleCommandProperty); set => SetValue(SubtitleCommandProperty, value); }
    public string MetadataText { get => (string)GetValue(MetadataTextProperty); set => SetValue(MetadataTextProperty, value); }
    public object? PrimaryActions { get => GetValue(PrimaryActionsProperty); set => SetValue(PrimaryActionsProperty, value); }
    public object? SubToggleContent { get => GetValue(SubToggleContentProperty); set => SetValue(SubToggleContentProperty, value); }
    public object? PanelContent { get => GetValue(PanelContentProperty); set => SetValue(PanelContentProperty, value); }

    // ── Visual sync ──

    private void ApplyArtShape()
    {
        if (ArtHost is null) return;
        ArtHost.CornerRadius = ArtShape == LibraryArtShape.Circle
            ? new CornerRadius(ArtSize / 2.0)
            : new CornerRadius(8);
    }

    private void UpdateArtVisibility()
    {
        if (HeroImage is null || PlaceholderIcon is null) return;
        var hasImage = !string.IsNullOrEmpty(ImageUrl);
        HeroImage.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;
        PlaceholderIcon.Visibility = hasImage ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateKicker()
    {
        if (KickerBlock is null) return;
        KickerBlock.Visibility = string.IsNullOrEmpty(Kicker) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateSubtitle()
    {
        if (SubtitleLink is null || SubtitlePlain is null) return;
        var hasText = !string.IsNullOrEmpty(SubtitleText);
        var linkable = hasText && SubtitleCommand is not null;
        SubtitleLink.Visibility = linkable ? Visibility.Visible : Visibility.Collapsed;
        SubtitlePlain.Visibility = (hasText && !linkable) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateMetadata()
    {
        if (MetaBlock is null) return;
        MetaBlock.Visibility = string.IsNullOrEmpty(MetadataText) ? Visibility.Collapsed : Visibility.Visible;
    }
}
