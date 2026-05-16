using System;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.Services.AddToPlaylist;
using Wavee.UI.WinUI.Styles;

namespace Wavee.UI.WinUI.Controls.AddToPlaylist;

/// <summary>
/// Reusable trailing "+" / "✓" chip for any row that isn't a
/// <see cref="Track.TrackItem"/>. Subscribes to the singleton
/// <see cref="IAddToPlaylistSession"/> and renders only when a session is
/// active AND <see cref="IsEligible"/> is true (cards set this false for
/// non-track results so episode / playlist / artist cards stay clean).
///
/// The chip is intentionally dumb about identity: callers pass
/// <see cref="TrackUri"/> + display fields, the chip handles the rest.
/// </summary>
public sealed partial class AddToPlaylistChip : UserControl
{
    private IAddToPlaylistSession? _session;
    private bool _isHooked;

    public AddToPlaylistChip()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    // ── Dependency Properties ──

    public static readonly DependencyProperty TrackUriProperty =
        DependencyProperty.Register(nameof(TrackUri), typeof(string), typeof(AddToPlaylistChip),
            new PropertyMetadata(null, (d, _) => ((AddToPlaylistChip)d).Apply()));

    public static readonly DependencyProperty TrackTitleProperty =
        DependencyProperty.Register(nameof(TrackTitle), typeof(string), typeof(AddToPlaylistChip),
            new PropertyMetadata(null));

    public static readonly DependencyProperty TrackArtistNameProperty =
        DependencyProperty.Register(nameof(TrackArtistName), typeof(string), typeof(AddToPlaylistChip),
            new PropertyMetadata(null));

    public static readonly DependencyProperty TrackImageUrlProperty =
        DependencyProperty.Register(nameof(TrackImageUrl), typeof(string), typeof(AddToPlaylistChip),
            new PropertyMetadata(null));

    /// <summary>Duration in milliseconds — passed through into the
    /// <see cref="PendingTrackEntry"/> so the flyout / submit have it
    /// available. Pass 0 if unknown.</summary>
    public static readonly DependencyProperty TrackDurationMsProperty =
        DependencyProperty.Register(nameof(TrackDurationMs), typeof(long), typeof(AddToPlaylistChip),
            new PropertyMetadata(0L));

    /// <summary>Set false when the row isn't a track (episode / playlist /
    /// artist / album card). The chip stays hidden regardless of session
    /// state. Defaults true so plain TrackItem-equivalents work without
    /// extra wiring.</summary>
    public static readonly DependencyProperty IsEligibleProperty =
        DependencyProperty.Register(nameof(IsEligible), typeof(bool), typeof(AddToPlaylistChip),
            new PropertyMetadata(true, (d, _) => ((AddToPlaylistChip)d).Apply()));

    public string? TrackUri
    {
        get => (string?)GetValue(TrackUriProperty);
        set => SetValue(TrackUriProperty, value);
    }
    public string? TrackTitle
    {
        get => (string?)GetValue(TrackTitleProperty);
        set => SetValue(TrackTitleProperty, value);
    }
    public string? TrackArtistName
    {
        get => (string?)GetValue(TrackArtistNameProperty);
        set => SetValue(TrackArtistNameProperty, value);
    }
    public string? TrackImageUrl
    {
        get => (string?)GetValue(TrackImageUrlProperty);
        set => SetValue(TrackImageUrlProperty, value);
    }
    public long TrackDurationMs
    {
        get => (long)GetValue(TrackDurationMsProperty);
        set => SetValue(TrackDurationMsProperty, value);
    }
    public bool IsEligible
    {
        get => (bool)GetValue(IsEligibleProperty);
        set => SetValue(IsEligibleProperty, value);
    }

    // ── Session hookup ──

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_isHooked) return;
        _session ??= Ioc.Default.GetService<IAddToPlaylistSession>();
        if (_session is null) return;
        _session.PropertyChanged += OnSessionPropertyChanged;
        if (_session.Pending is INotifyCollectionChanged pendingNcc)
            pendingNcc.CollectionChanged += OnPendingCollectionChanged;
        _isHooked = true;
        Apply();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_isHooked || _session is null) return;
        _session.PropertyChanged -= OnSessionPropertyChanged;
        if (_session.Pending is INotifyCollectionChanged pendingNcc)
            pendingNcc.CollectionChanged -= OnPendingCollectionChanged;
        _isHooked = false;
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
        => DispatchApply();

    private void OnPendingCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => DispatchApply();

    private void DispatchApply()
    {
        if (DispatcherQueue?.HasThreadAccess == true) Apply();
        else DispatcherQueue?.TryEnqueue(Apply);
    }

    private void Apply()
    {
        var active = _session?.IsActive == true && IsEligible && !string.IsNullOrEmpty(TrackUri);
        Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (!active || ChipGlyph is null) return;
        ChipGlyph.Glyph = _session!.Contains(TrackUri!)
            ? FluentGlyphs.CheckMark
            : FluentGlyphs.Add;
    }

    private void OnClick(object sender, RoutedEventArgs e)
    {
        if (_session is null || !_session.IsActive) return;
        var uri = TrackUri;
        if (string.IsNullOrEmpty(uri)) return;
        var entry = new PendingTrackEntry(
            Uri: uri!,
            Title: TrackTitle ?? string.Empty,
            ArtistName: TrackArtistName,
            ImageUrl: TrackImageUrl,
            Duration: TimeSpan.FromMilliseconds(TrackDurationMs));
        _session.Toggle(entry);
    }
}
