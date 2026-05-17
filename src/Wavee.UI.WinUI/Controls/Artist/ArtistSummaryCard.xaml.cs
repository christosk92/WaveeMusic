using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Input;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Wavee.UI.Helpers;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Styles;

namespace Wavee.UI.WinUI.Controls.Artist;

/// <summary>
/// Shared "About the artist" summary card. Drop it into any page that wants
/// the canonical avatar + name + bio + Follow pill visual. Self-contained:
/// the AI bio fallback (âœ¨ sparkle + on-device Phi Silica summary when
/// Spotify's bio is empty) is owned by the control â€” pages opt in via
/// <see cref="EnableAiSummary"/>. Page-specific extras can be appended via
/// <see cref="AdditionalContent"/>.
/// </summary>
public sealed partial class ArtistSummaryCard : UserControl
{
    // â”€â”€ Data DPs â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public static readonly DependencyProperty ArtistUriProperty =
        DependencyProperty.Register(nameof(ArtistUri), typeof(string), typeof(ArtistSummaryCard),
            new PropertyMetadata(null, OnAiInputChanged));

    public static readonly DependencyProperty ArtistNameProperty =
        DependencyProperty.Register(nameof(ArtistName), typeof(string), typeof(ArtistSummaryCard),
            new PropertyMetadata(null, OnArtistNameChanged));

    public static readonly DependencyProperty ArtistImageUrlProperty =
        DependencyProperty.Register(nameof(ArtistImageUrl), typeof(string), typeof(ArtistSummaryCard),
            new PropertyMetadata(null, OnArtistImageUrlChanged));

    public static readonly DependencyProperty BioExcerptProperty =
        DependencyProperty.Register(nameof(BioExcerpt), typeof(string), typeof(ArtistSummaryCard),
            new PropertyMetadata(null, OnBioExcerptChanged));

    public static readonly DependencyProperty IsVerifiedProperty =
        DependencyProperty.Register(nameof(IsVerified), typeof(bool), typeof(ArtistSummaryCard),
            new PropertyMetadata(false, OnIsVerifiedChanged));

    public static readonly DependencyProperty AccentBrushProperty =
        DependencyProperty.Register(nameof(AccentBrush), typeof(Brush), typeof(ArtistSummaryCard),
            new PropertyMetadata(null, OnAccentBrushChanged));

    public static readonly DependencyProperty EyebrowTextProperty =
        DependencyProperty.Register(nameof(EyebrowText), typeof(string), typeof(ArtistSummaryCard),
            new PropertyMetadata("ABOUT THE ARTIST", OnEyebrowTextChanged));

    public static readonly DependencyProperty IsFollowingProperty =
        DependencyProperty.Register(nameof(IsFollowing), typeof(bool), typeof(ArtistSummaryCard),
            new PropertyMetadata(false, OnIsFollowingChanged));

    public static readonly DependencyProperty ToggleFollowCommandProperty =
        DependencyProperty.Register(nameof(ToggleFollowCommand), typeof(ICommand), typeof(ArtistSummaryCard),
            new PropertyMetadata(null));

    public static readonly DependencyProperty AdditionalContentProperty =
        DependencyProperty.Register(nameof(AdditionalContent), typeof(object), typeof(ArtistSummaryCard),
            new PropertyMetadata(null, OnAdditionalContentChanged));

    // â”€â”€ AI-summary DPs â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public static readonly DependencyProperty EnableAiSummaryProperty =
        DependencyProperty.Register(nameof(EnableAiSummary), typeof(bool), typeof(ArtistSummaryCard),
            new PropertyMetadata(false, OnAiInputChanged));

    public static readonly DependencyProperty SummaryGenresProperty =
        DependencyProperty.Register(nameof(SummaryGenres), typeof(IReadOnlyList<string>), typeof(ArtistSummaryCard),
            new PropertyMetadata(null));

    public static readonly DependencyProperty SummaryMonthlyListenersDisplayProperty =
        DependencyProperty.Register(nameof(SummaryMonthlyListenersDisplay), typeof(string), typeof(ArtistSummaryCard),
            new PropertyMetadata(null));

    public static readonly DependencyProperty SummaryTopTrackNamesProperty =
        DependencyProperty.Register(nameof(SummaryTopTrackNames), typeof(IReadOnlyList<string>), typeof(ArtistSummaryCard),
            new PropertyMetadata(null));

    // â”€â”€ CLR wrappers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public string? ArtistUri { get => (string?)GetValue(ArtistUriProperty); set => SetValue(ArtistUriProperty, value); }
    public string? ArtistName { get => (string?)GetValue(ArtistNameProperty); set => SetValue(ArtistNameProperty, value); }
    public string? ArtistImageUrl { get => (string?)GetValue(ArtistImageUrlProperty); set => SetValue(ArtistImageUrlProperty, value); }
    public string? BioExcerpt { get => (string?)GetValue(BioExcerptProperty); set => SetValue(BioExcerptProperty, value); }
    public bool IsVerified { get => (bool)GetValue(IsVerifiedProperty); set => SetValue(IsVerifiedProperty, value); }
    public Brush? AccentBrush { get => (Brush?)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public string EyebrowText { get => (string)GetValue(EyebrowTextProperty); set => SetValue(EyebrowTextProperty, value); }
    public bool IsFollowing { get => (bool)GetValue(IsFollowingProperty); set => SetValue(IsFollowingProperty, value); }
    public ICommand? ToggleFollowCommand { get => (ICommand?)GetValue(ToggleFollowCommandProperty); set => SetValue(ToggleFollowCommandProperty, value); }
    public object? AdditionalContent { get => GetValue(AdditionalContentProperty); set => SetValue(AdditionalContentProperty, value); }
    public bool EnableAiSummary { get => (bool)GetValue(EnableAiSummaryProperty); set => SetValue(EnableAiSummaryProperty, value); }
    public IReadOnlyList<string>? SummaryGenres { get => (IReadOnlyList<string>?)GetValue(SummaryGenresProperty); set => SetValue(SummaryGenresProperty, value); }
    public string? SummaryMonthlyListenersDisplay { get => (string?)GetValue(SummaryMonthlyListenersDisplayProperty); set => SetValue(SummaryMonthlyListenersDisplayProperty, value); }
    public IReadOnlyList<string>? SummaryTopTrackNames { get => (IReadOnlyList<string>?)GetValue(SummaryTopTrackNamesProperty); set => SetValue(SummaryTopTrackNamesProperty, value); }

    // â”€â”€ State â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private CancellationTokenSource? _aiCts;

    public ArtistSummaryCard()
    {
        InitializeComponent();
        // Initial follow glyph/text so the button renders correctly before
        // any DP-changed callback fires.
        ApplyFollowVisual(false);
        // Hand cursor on hover â€” propagates from this UserControl down to
        // every descendant. Buttons don't override cursor in WinUI 3 by
        // default, so the Follow pill also shows the hand cursor (which is
        // the correct affordance â€” clicking does an action).
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _aiCts?.Cancel();
        _aiCts = null;
    }

    // â”€â”€ DP callbacks â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private static void OnArtistNameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ArtistSummaryCard card) return;
        var name = (string?)e.NewValue ?? string.Empty;
        card.NameTextBlock.Text = name;
        card.AvatarPicture.DisplayName = name;
        // Artist switched â€” re-trigger AI flow if applicable. (Name change
        // alone doesn't guarantee the artist changed, but it's a cheap
        // additional trigger; OnAiInputChanged handles the no-op fast path.)
        card.MaybeTriggerAiSummary();
    }

    private static void OnArtistImageUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ArtistSummaryCard card) return;
        var httpsUrl = SpotifyImageHelper.ToHttpsUrl(e.NewValue as string);
        card.AvatarPicture.ProfilePicture = string.IsNullOrEmpty(httpsUrl)
            ? null
            : new BitmapImage(new Uri(httpsUrl))
              {
                  DecodePixelWidth = 112,
                  DecodePixelType = DecodePixelType.Logical,
              };
    }

    private static void OnBioExcerptChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ArtistSummaryCard card) return;
        var bio = e.NewValue as string;
        card.BioTextBlock.Text = bio ?? string.Empty;
        // Externally-set non-empty bio (Spotify's real bio arrived) supersedes
        // any AI summary that may have been shown â€” drop the sparkle.
        if (!string.IsNullOrWhiteSpace(bio))
            card.AiSparkle.Visibility = Visibility.Collapsed;
        // If the bio became empty AND AI is enabled, kick off a summary.
        card.MaybeTriggerAiSummary();
    }

    private static void OnIsVerifiedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ArtistSummaryCard card) return;
        card.VerifiedGlyph.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void OnAccentBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ArtistSummaryCard card) return;
        var brush = e.NewValue as Brush;
        if (brush is not null)
        {
            card.VerifiedGlyph.Foreground = brush;
            card.AiSparkle.Foreground = brush;
        }
    }

    private static void OnEyebrowTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ArtistSummaryCard card)
            card.EyebrowTextBlock.Text = e.NewValue as string ?? string.Empty;
    }

    private static void OnIsFollowingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ArtistSummaryCard card)
            card.ApplyFollowVisual((bool)e.NewValue);
    }

    private void ApplyFollowVisual(bool following)
    {
        // Glyph constants live in FluentGlyphs (memory `feedback_fluent_glyphs`
        // â€” never inline PUA literals in .cs files).
        FollowGlyph.Glyph = following ? FluentGlyphs.HeartFilled : FluentGlyphs.HeartOutline;
        FollowLabel.Text = following ? "Following" : "Follow";
    }

    private static void OnAdditionalContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ArtistSummaryCard card) return;
        card.AdditionalContentHost.Content = e.NewValue;
        card.AdditionalContentHost.Visibility = e.NewValue is null
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private static void OnAiInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ArtistSummaryCard card) card.MaybeTriggerAiSummary();
    }

    private void FollowButton_Click(object sender, RoutedEventArgs e)
    {
        // Forward to the consumer's command â€” the VM owns the actual save-state
        // toggle (mirrors ArtistViewModel.ToggleFollow). The IsFollowing DP
        // round-trips back through the binding after the VM mutates it.
        if (ToggleFollowCommand?.CanExecute(null) == true)
            ToggleFollowCommand.Execute(null);
    }

    // â”€â”€ Card-surface click + hover â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private void CardSurface_Tapped(object sender, TappedRoutedEventArgs e)
    {
        // Filter out taps that originated inside the Follow button â€” that
        // button handles its own Click and we don't want the card-surface
        // tap to ALSO navigate. Walk up the visual tree from the original
        // source; if we hit FollowButton before the CardSurface, ignore.
        if (e.OriginalSource is DependencyObject origin && IsDescendantOf(origin, FollowButton))
            return;

        if (string.IsNullOrEmpty(ArtistUri)) return;
        var openInNewTab = NavigationHelpers.IsCtrlPressed();
        NavigationHelpers.OpenArtist(ArtistUri, ArtistName ?? "Artist", openInNewTab);
        e.Handled = true;
    }

    private void CardSurface_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        // Subtle background lift on hover. Mirrors the affordance the rest
        // of the app uses (ContentCard, ArtistPillCard) â€” lighter card-fill
        // tier signals "this is clickable".
        if (App.Current?.Resources["CardBackgroundFillColorSecondaryBrush"] is Brush hoverBrush)
            CardSurface.Background = hoverBrush;
    }

    private void CardSurface_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (App.Current?.Resources["CardBackgroundFillColorDefaultBrush"] is Brush restBrush)
            CardSurface.Background = restBrush;
    }

    private static bool IsDescendantOf(DependencyObject candidate, DependencyObject ancestor)
    {
        var node = candidate;
        while (node is not null)
        {
            if (ReferenceEquals(node, ancestor)) return true;
            node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    // â”€â”€ AI bio fallback â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private async void MaybeTriggerAiSummary()
    {
        // Cancel any in-flight summary for a previous artist before
        // potentially starting a new one. Cheap when nothing is in flight.
        _aiCts?.Cancel();
        _aiCts = null;

        if (!EnableAiSummary) return;
        if (!string.IsNullOrWhiteSpace(BioExcerpt)) return;          // Spotify bio wins
        if (string.IsNullOrEmpty(ArtistUri) || string.IsNullOrEmpty(ArtistName)) return;

        var caps = Ioc.Default.GetService<AiCapabilities>();
        if (caps is null || !caps.IsArtistBioSummarizeEnabled) return;

        var summarizer = Ioc.Default.GetService<ArtistBioSummarizer>();
        if (summarizer is null) return;

        var cts = _aiCts = new CancellationTokenSource();
        var capturedUri = ArtistUri;
        try
        {
            var result = await summarizer.SummarizeBioAsync(
                capturedUri!, ArtistName!,
                genres: SummaryGenres,
                monthlyListenersDisplay: SummaryMonthlyListenersDisplay,
                topTrackNames: SummaryTopTrackNames,
                deltaProgress: null,
                ct: cts.Token).ConfigureAwait(true);

            // Stale-result guard: if the user navigated to a different artist
            // while the summary was inflight, drop this result.
            if (cts.IsCancellationRequested) return;
            if (!string.Equals(capturedUri, ArtistUri, StringComparison.Ordinal)) return;

            if (result.Kind == LyricsAiResultKind.Ok && !string.IsNullOrWhiteSpace(result.Text))
            {
                // Set BioTextBlock directly rather than mutating the DP â€” that
                // would re-enter OnBioExcerptChanged â†’ MaybeTriggerAiSummary
                // and clear the sparkle.
                BioTextBlock.Text = result.Text;
                AiSparkle.Visibility = Visibility.Visible;
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the user navigates away or the artist changes.
        }
        catch (Exception)
        {
            // Empty bio + no sparkle is the graceful fallback. We don't surface
            // an error chrome on this affordance â€” the rest of the page is the
            // primary surface; the bio is supplementary.
        }
    }
}
