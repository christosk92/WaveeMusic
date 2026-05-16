using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Wavee.UI.WinUI.Controls.Playlist;

/// <summary>
/// Compact "shy" pill for PlaylistPage — surfaces cover thumb, name, owner,
/// and Play button. Designed as the
/// <see cref="HeroHeader.ShyHeaderController"/>'s morph target: the matched-id
/// leaves inside this control (playlist-name / playlist-owner + card-shell on
/// the outer Border) interpolate with the hero banner overlay's corresponding
/// leaves when the user scrolls past the banner. Mirrors
/// <see cref="HeroHeader.ArtistShyPill"/>.
/// </summary>
public sealed partial class PlaylistShyPill : UserControl
{
    public PlaylistShyPill() { InitializeComponent(); }

    public static readonly DependencyProperty PlaylistImageUrlProperty =
        DependencyProperty.Register(nameof(PlaylistImageUrl), typeof(string), typeof(PlaylistShyPill),
            new PropertyMetadata(null));
    public string? PlaylistImageUrl
    {
        get => (string?)GetValue(PlaylistImageUrlProperty);
        set => SetValue(PlaylistImageUrlProperty, value);
    }

    public static readonly DependencyProperty PlaylistNameProperty =
        DependencyProperty.Register(nameof(PlaylistName), typeof(string), typeof(PlaylistShyPill),
            new PropertyMetadata(null));
    public string? PlaylistName
    {
        get => (string?)GetValue(PlaylistNameProperty);
        set => SetValue(PlaylistNameProperty, value);
    }

    public static readonly DependencyProperty OwnerNameProperty =
        DependencyProperty.Register(nameof(OwnerName), typeof(string), typeof(PlaylistShyPill),
            new PropertyMetadata(null));
    public string? OwnerName
    {
        get => (string?)GetValue(OwnerNameProperty);
        set => SetValue(OwnerNameProperty, value);
    }

    public static readonly DependencyProperty IsPlaylistContextPlayingProperty =
        DependencyProperty.Register(nameof(IsPlaylistContextPlaying), typeof(bool), typeof(PlaylistShyPill),
            new PropertyMetadata(false));
    public bool IsPlaylistContextPlaying
    {
        get => (bool)GetValue(IsPlaylistContextPlayingProperty);
        set => SetValue(IsPlaylistContextPlayingProperty, value);
    }

    public static readonly DependencyProperty IsPlayPendingProperty =
        DependencyProperty.Register(nameof(IsPlayPending), typeof(bool), typeof(PlaylistShyPill),
            new PropertyMetadata(false));
    public bool IsPlayPending
    {
        get => (bool)GetValue(IsPlayPendingProperty);
        set => SetValue(IsPlayPendingProperty, value);
    }

    public static readonly DependencyProperty PaletteAccentPillBrushProperty =
        DependencyProperty.Register(nameof(PaletteAccentPillBrush), typeof(Brush), typeof(PlaylistShyPill),
            new PropertyMetadata(null));
    public Brush? PaletteAccentPillBrush
    {
        get => (Brush?)GetValue(PaletteAccentPillBrushProperty);
        set => SetValue(PaletteAccentPillBrushProperty, value);
    }

    public static readonly DependencyProperty PaletteAccentPillForegroundBrushProperty =
        DependencyProperty.Register(nameof(PaletteAccentPillForegroundBrush), typeof(Brush), typeof(PlaylistShyPill),
            new PropertyMetadata(null));
    public Brush? PaletteAccentPillForegroundBrush
    {
        get => (Brush?)GetValue(PaletteAccentPillForegroundBrushProperty);
        set => SetValue(PaletteAccentPillForegroundBrushProperty, value);
    }

    public static readonly DependencyProperty PlayCommandProperty =
        DependencyProperty.Register(nameof(PlayCommand), typeof(ICommand), typeof(PlaylistShyPill),
            new PropertyMetadata(null));
    public ICommand? PlayCommand
    {
        get => (ICommand?)GetValue(PlayCommandProperty);
        set => SetValue(PlayCommandProperty, value);
    }
}
