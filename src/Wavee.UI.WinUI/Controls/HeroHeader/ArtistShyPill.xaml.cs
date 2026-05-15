using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Wavee.UI.WinUI.Controls.HeroHeader;

/// <summary>
/// Compact "shy" pill for the artist detail page — surfaces avatar, name,
/// monthly-listeners line, and Play / Follow buttons. Designed as the
/// <see cref="ShyHeaderController"/>'s morph target: the matched-id leaves
/// inside this control (artist-avatar / artist-name / artist-meta /
/// artist-play / artist-follow + card-shell on the outer Border) interpolate
/// with the hero overlay's corresponding leaves when the user scrolls past
/// the hero. Extracted out of ArtistPage so other artist surfaces can
/// reuse the same visual.
/// </summary>
public sealed partial class ArtistShyPill : UserControl
{
    public ArtistShyPill() { InitializeComponent(); }

    public static readonly DependencyProperty ArtistImageUrlProperty =
        DependencyProperty.Register(nameof(ArtistImageUrl), typeof(string), typeof(ArtistShyPill),
            new PropertyMetadata(null));
    public string? ArtistImageUrl
    {
        get => (string?)GetValue(ArtistImageUrlProperty);
        set => SetValue(ArtistImageUrlProperty, value);
    }

    public static readonly DependencyProperty ArtistNameProperty =
        DependencyProperty.Register(nameof(ArtistName), typeof(string), typeof(ArtistShyPill),
            new PropertyMetadata(null));
    public string? ArtistName
    {
        get => (string?)GetValue(ArtistNameProperty);
        set => SetValue(ArtistNameProperty, value);
    }

    public static readonly DependencyProperty MonthlyListenersTextProperty =
        DependencyProperty.Register(nameof(MonthlyListenersText), typeof(string), typeof(ArtistShyPill),
            new PropertyMetadata(null));
    public string? MonthlyListenersText
    {
        get => (string?)GetValue(MonthlyListenersTextProperty);
        set => SetValue(MonthlyListenersTextProperty, value);
    }

    public static readonly DependencyProperty IsArtistContextPlayingProperty =
        DependencyProperty.Register(nameof(IsArtistContextPlaying), typeof(bool), typeof(ArtistShyPill),
            new PropertyMetadata(false));
    public bool IsArtistContextPlaying
    {
        get => (bool)GetValue(IsArtistContextPlayingProperty);
        set => SetValue(IsArtistContextPlayingProperty, value);
    }

    public static readonly DependencyProperty IsPlayPendingProperty =
        DependencyProperty.Register(nameof(IsPlayPending), typeof(bool), typeof(ArtistShyPill),
            new PropertyMetadata(false));
    public bool IsPlayPending
    {
        get => (bool)GetValue(IsPlayPendingProperty);
        set => SetValue(IsPlayPendingProperty, value);
    }

    public static readonly DependencyProperty IsFollowingProperty =
        DependencyProperty.Register(nameof(IsFollowing), typeof(bool), typeof(ArtistShyPill),
            new PropertyMetadata(false));
    public bool IsFollowing
    {
        get => (bool)GetValue(IsFollowingProperty);
        set => SetValue(IsFollowingProperty, value);
    }

    public static readonly DependencyProperty ArtistPlayButtonTextProperty =
        DependencyProperty.Register(nameof(ArtistPlayButtonText), typeof(string), typeof(ArtistShyPill),
            new PropertyMetadata(null));
    public string? ArtistPlayButtonText
    {
        get => (string?)GetValue(ArtistPlayButtonTextProperty);
        set => SetValue(ArtistPlayButtonTextProperty, value);
    }

    public static readonly DependencyProperty PaletteAccentPillBrushProperty =
        DependencyProperty.Register(nameof(PaletteAccentPillBrush), typeof(Brush), typeof(ArtistShyPill),
            new PropertyMetadata(null));
    public Brush? PaletteAccentPillBrush
    {
        get => (Brush?)GetValue(PaletteAccentPillBrushProperty);
        set => SetValue(PaletteAccentPillBrushProperty, value);
    }

    public static readonly DependencyProperty PaletteAccentPillForegroundBrushProperty =
        DependencyProperty.Register(nameof(PaletteAccentPillForegroundBrush), typeof(Brush), typeof(ArtistShyPill),
            new PropertyMetadata(null));
    public Brush? PaletteAccentPillForegroundBrush
    {
        get => (Brush?)GetValue(PaletteAccentPillForegroundBrushProperty);
        set => SetValue(PaletteAccentPillForegroundBrushProperty, value);
    }

    public static readonly DependencyProperty PlayCommandProperty =
        DependencyProperty.Register(nameof(PlayCommand), typeof(ICommand), typeof(ArtistShyPill),
            new PropertyMetadata(null));
    public ICommand? PlayCommand
    {
        get => (ICommand?)GetValue(PlayCommandProperty);
        set => SetValue(PlayCommandProperty, value);
    }

    public static readonly DependencyProperty ToggleFollowCommandProperty =
        DependencyProperty.Register(nameof(ToggleFollowCommand), typeof(ICommand), typeof(ArtistShyPill),
            new PropertyMetadata(null));
    public ICommand? ToggleFollowCommand
    {
        get => (ICommand?)GetValue(ToggleFollowCommandProperty);
        set => SetValue(ToggleFollowCommandProperty, value);
    }
}
