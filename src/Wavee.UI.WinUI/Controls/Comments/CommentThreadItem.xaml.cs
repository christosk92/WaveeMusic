using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Wavee.UI.WinUI.ViewModels;
using Windows.Foundation;

namespace Wavee.UI.WinUI.Controls.Comments;

/// <summary>
/// Single-comment renderer used by <see cref="CommentsList"/>. Binds a
/// <see cref="PodcastCommentViewModel"/> via the <see cref="Comment"/> DP and
/// wires up reactions, replies, and the per-comment reply composer. Set
/// <see cref="CompactMode"/> to <c>true</c> to suppress the replies expander
/// and reply composer (used by the right-sidebar comments list).
/// </summary>
public sealed partial class CommentThreadItem : UserControl
{
    public static readonly DependencyProperty CommentProperty =
        DependencyProperty.Register(
            nameof(Comment), typeof(PodcastCommentViewModel), typeof(CommentThreadItem),
            new PropertyMetadata(null, OnCommentChanged));

    public static readonly DependencyProperty CompactModeProperty =
        DependencyProperty.Register(
            nameof(CompactMode), typeof(bool), typeof(CommentThreadItem),
            new PropertyMetadata(false, OnCompactModeChanged));

    public PodcastCommentViewModel? Comment
    {
        get => (PodcastCommentViewModel?)GetValue(CommentProperty);
        set => SetValue(CommentProperty, value);
    }

    public bool CompactMode
    {
        get => (bool)GetValue(CompactModeProperty);
        set => SetValue(CompactModeProperty, value);
    }

    /// <summary>Fired when the user taps the reactions chip on the top-level comment.</summary>
    public event TypedEventHandler<CommentThreadItem, PodcastCommentViewModel>? ShowReactionsRequested;
    /// <summary>Fired when the user taps the reactions chip on a nested reply.</summary>
    public event TypedEventHandler<CommentThreadItem, PodcastReplyViewModel>? ShowReplyReactionsRequested;
    /// <summary>Fired when the user taps the per-comment "Reply" submit button. Host
    /// is responsible for consent + invoking <c>SubmitReplyCommand</c>.</summary>
    public event TypedEventHandler<CommentThreadItem, PodcastCommentViewModel>? ReplySubmitRequested;

    private PodcastCommentViewModel? _attachedComment;

    public CommentThreadItem()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private static void OnCommentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CommentThreadItem item) item.AttachComment(e.NewValue as PodcastCommentViewModel);
    }

    private static void OnCompactModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CommentThreadItem item) item.ApplyCompactMode();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyCompactMode();
        AttachComment(Comment);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachComment();
    }

    private void ApplyCompactMode()
    {
        var compact = CompactMode;
        AvatarColumn.Width = new GridLength(compact ? 28 : 36);
        AvatarPicture.Width = AvatarPicture.Height = compact ? 28 : 36;
        BodyText.MaxLines = compact ? 6 : 0; // 0 = unlimited
        ReplyHyperlink.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ReplyComposerPanel.Visibility = compact ? Visibility.Collapsed : ReplyComposerPanel.Visibility;
        RepliesExpander.Visibility = compact ? Visibility.Collapsed : RepliesExpander.Visibility;
    }

    private void AttachComment(PodcastCommentViewModel? comment)
    {
        DetachComment();

        _attachedComment = comment;
        if (comment is null)
        {
            ClearVisuals();
            return;
        }

        AvatarPicture.DisplayName = comment.Data?.AuthorName ?? "";
        AvatarPicture.ProfilePicture = TryLoadImage(comment.Data?.AuthorImageUrl);

        UpdateHeader();
        BodyText.Text = comment.Data?.Text ?? "";

        UpdateReactions();
        UpdateReplyComposer();
        UpdateReplyStatus();
        UpdateRepliesSection();

        comment.PropertyChanged += OnCommentPropertyChanged;
    }

    private void DetachComment()
    {
        if (_attachedComment is null) return;
        _attachedComment.PropertyChanged -= OnCommentPropertyChanged;
        _attachedComment = null;
    }

    private void ClearVisuals()
    {
        BodyText.Text = "";
        HeaderText.Inlines.Clear();
        ReactionsChipButton.Visibility = Visibility.Collapsed;
        ReactButton.IsEnabled = false;
        ReplyComposerPanel.Visibility = Visibility.Collapsed;
        ReplyStatusText.Visibility = Visibility.Collapsed;
        RepliesExpander.Visibility = Visibility.Collapsed;
        RepliesRepeater.ItemsSource = null;
    }

    private static ImageSource? TryLoadImage(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        try { return new BitmapImage(new Uri(url)); }
        catch { return null; }
    }

    private void UpdateHeader()
    {
        HeaderText.Inlines.Clear();
        if (_attachedComment?.Data is not { } data) return;

        var name = new Microsoft.UI.Xaml.Documents.Run
        {
            Text = data.AuthorName ?? "",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Microsoft.UI.Xaml.Media.Brush?)Application.Current.Resources["TextFillColorPrimaryBrush"],
        };
        var sep = new Microsoft.UI.Xaml.Documents.Run
        {
            Text = " · ",
            Foreground = (Microsoft.UI.Xaml.Media.Brush?)Application.Current.Resources["TextFillColorTertiaryBrush"],
        };
        var when = new Microsoft.UI.Xaml.Documents.Run
        {
            Text = data.CreatedAtFormatted ?? "",
            Foreground = (Microsoft.UI.Xaml.Media.Brush?)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        HeaderText.Inlines.Add(name);
        HeaderText.Inlines.Add(sep);
        HeaderText.Inlines.Add(when);
    }

    private void UpdateReactions()
    {
        if (_attachedComment is null) return;
        ReactionsChipButton.Visibility = _attachedComment.HasReactions ? Visibility.Visible : Visibility.Collapsed;
        ReactionEmojiText.Text = _attachedComment.TopReactionEmojiString ?? "";
        ReactionsLabelText.Text = _attachedComment.ReactionsLabel ?? "";
        ReactButton.IsEnabled = _attachedComment.CanReact;
    }

    private void UpdateReplyComposer()
    {
        if (_attachedComment is null) return;
        if (CompactMode)
        {
            ReplyComposerPanel.Visibility = Visibility.Collapsed;
            return;
        }

        ReplyComposerPanel.Visibility = _attachedComment.IsReplyComposerOpen ? Visibility.Visible : Visibility.Collapsed;
        ReplyTextBox.PlaceholderText = _attachedComment.ReplyPlaceholder ?? "";
        if (ReplyTextBox.Text != (_attachedComment.ReplyDraft ?? ""))
            ReplyTextBox.Text = _attachedComment.ReplyDraft ?? "";
        ReplyCharCount.Text = _attachedComment.ReplyCharacterCount ?? "";
        ReplySubmitButton.IsEnabled = _attachedComment.CanSubmitReply;
    }

    private void UpdateReplyStatus()
    {
        if (_attachedComment is null) return;
        var hasStatus = _attachedComment.HasReplyComposerStatus;
        ReplyStatusText.Visibility = hasStatus ? Visibility.Visible : Visibility.Collapsed;
        ReplyStatusText.Text = _attachedComment.ReplyComposerStatus ?? "";
    }

    private void UpdateRepliesSection()
    {
        if (_attachedComment is null) return;
        if (CompactMode)
        {
            RepliesExpander.Visibility = Visibility.Collapsed;
            return;
        }

        RepliesExpander.Visibility = _attachedComment.HasReplies ? Visibility.Visible : Visibility.Collapsed;
        if (RepliesExpander.IsExpanded != _attachedComment.IsRepliesExpanded)
            RepliesExpander.IsExpanded = _attachedComment.IsRepliesExpanded;
        ReplyAvatarStack.ItemsSource = _attachedComment.TopReplyAvatarItems;
        ReplyAvatarStack.Visibility = _attachedComment.HasTopReplyAvatars ? Visibility.Visible : Visibility.Collapsed;
        RepliesLabelText.Text = _attachedComment.RepliesLabel ?? "";
        if (!ReferenceEquals(RepliesRepeater.ItemsSource, _attachedComment.Replies))
            RepliesRepeater.ItemsSource = _attachedComment.Replies;
        RepliesLoadingPanel.Visibility = _attachedComment.IsLoadingReplies ? Visibility.Visible : Visibility.Collapsed;
        LoadMoreRepliesButton.Visibility = _attachedComment.HasMoreReplies ? Visibility.Visible : Visibility.Collapsed;
        LoadMoreRepliesButton.Command = _attachedComment.LoadMoreRepliesCommand;
    }

    private void OnCommentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_attachedComment is null) return;
        switch (e.PropertyName)
        {
            case nameof(PodcastCommentViewModel.HasReactions):
            case nameof(PodcastCommentViewModel.TopReactionEmojiString):
            case nameof(PodcastCommentViewModel.ReactionsLabel):
            case nameof(PodcastCommentViewModel.CanReact):
                UpdateReactions();
                break;
            case nameof(PodcastCommentViewModel.IsReplyComposerOpen):
            case nameof(PodcastCommentViewModel.ReplyDraft):
            case nameof(PodcastCommentViewModel.ReplyCharacterCount):
            case nameof(PodcastCommentViewModel.CanSubmitReply):
            case nameof(PodcastCommentViewModel.ReplyPlaceholder):
                UpdateReplyComposer();
                break;
            case nameof(PodcastCommentViewModel.ReplyComposerStatus):
            case nameof(PodcastCommentViewModel.HasReplyComposerStatus):
                UpdateReplyStatus();
                break;
            case nameof(PodcastCommentViewModel.HasReplies):
            case nameof(PodcastCommentViewModel.IsRepliesExpanded):
            case nameof(PodcastCommentViewModel.TopReplyAvatarItems):
            case nameof(PodcastCommentViewModel.HasTopReplyAvatars):
            case nameof(PodcastCommentViewModel.RepliesLabel):
            case nameof(PodcastCommentViewModel.Replies):
            case nameof(PodcastCommentViewModel.IsLoadingReplies):
            case nameof(PodcastCommentViewModel.HasMoreReplies):
            case nameof(PodcastCommentViewModel.LoadMoreRepliesCommand):
                UpdateRepliesSection();
                break;
        }
    }

    // ── Event handlers ───────────────────────────────────────────────────

    private void ReactionsChip_Click(object sender, RoutedEventArgs e)
    {
        if (_attachedComment is { } c)
            ShowReactionsRequested?.Invoke(this, c);
    }

    private void ReactionMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_attachedComment is null) return;
        if (sender is MenuFlyoutItem item && item.Tag is string emoji)
            _attachedComment.ReactCommand.Execute(emoji);
    }

    private void ReplyHyperlink_Click(object sender, RoutedEventArgs e)
    {
        _attachedComment?.ShowReplyComposerCommand.Execute(null);
    }

    private void ReplyCancel_Click(object sender, RoutedEventArgs e)
    {
        _attachedComment?.CancelReplyCommand.Execute(null);
    }

    private void ReplySubmit_Click(object sender, RoutedEventArgs e)
    {
        if (_attachedComment is { } c)
            ReplySubmitRequested?.Invoke(this, c);
    }

    private void ReplyTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_attachedComment is null) return;
        if (_attachedComment.ReplyDraft != ReplyTextBox.Text)
            _attachedComment.ReplyDraft = ReplyTextBox.Text;
    }

    private void ReplyReactionsChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is PodcastReplyViewModel reply)
            ShowReplyReactionsRequested?.Invoke(this, reply);
    }
}
