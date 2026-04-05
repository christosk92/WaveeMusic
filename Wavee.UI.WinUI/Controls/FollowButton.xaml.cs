using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Controls;

/// <summary>
/// Self-contained follow/unfollow button. Resolves ITrackLikeService from DI,
/// manages its own state, and stays in sync via SaveStateChanged events.
/// Usage: &lt;controls:FollowButton ItemUri="{x:Bind ArtistId}"/&gt;
/// </summary>
public sealed partial class FollowButton : UserControl
{
    private ITrackLikeService? _likeService;
    private readonly DispatcherQueue _dispatcherQueue;

    public static readonly DependencyProperty ItemUriProperty =
        DependencyProperty.Register(nameof(ItemUri), typeof(string), typeof(FollowButton),
            new PropertyMetadata(null, OnItemUriChanged));

    public static readonly DependencyProperty ItemTypeProperty =
        DependencyProperty.Register(nameof(ItemType), typeof(SavedItemType), typeof(FollowButton),
            new PropertyMetadata(SavedItemType.Artist, OnItemUriChanged));

    public string? ItemUri
    {
        get => (string?)GetValue(ItemUriProperty);
        set => SetValue(ItemUriProperty, value);
    }

    public SavedItemType ItemType
    {
        get => (SavedItemType)GetValue(ItemTypeProperty);
        set => SetValue(ItemTypeProperty, value);
    }

    public FollowButton()
    {
        InitializeComponent();
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _likeService = Ioc.Default.GetService<ITrackLikeService>();
        if (_likeService != null)
            _likeService.SaveStateChanged += OnSaveStateChanged;
        RefreshState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_likeService != null)
            _likeService.SaveStateChanged -= OnSaveStateChanged;
    }

    private void OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(ItemUri) || _likeService == null) return;
        var isSaved = _likeService.IsSaved(ItemType, ItemUri);
        _likeService.ToggleSave(ItemType, ItemUri, isSaved);
    }

    private void OnSaveStateChanged()
    {
        _dispatcherQueue.TryEnqueue(RefreshState);
    }

    private void RefreshState()
    {
        var isFollowing = !string.IsNullOrEmpty(ItemUri)
            && _likeService != null
            && _likeService.IsSaved(ItemType, ItemUri);
        UpdateVisual(isFollowing);
    }

    private void UpdateVisual(bool isFollowing)
    {
        FollowIcon.Glyph = isFollowing ? "\uE8FB" : "\uEB51";
        FollowText.Text = isFollowing ? "Following" : "Follow";
        ToolTipService.SetToolTip(InternalButton, isFollowing ? "Unfollow" : "Follow");
    }

    private static void OnItemUriChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FollowButton btn && btn.IsLoaded)
            btn.RefreshState();
    }
}
