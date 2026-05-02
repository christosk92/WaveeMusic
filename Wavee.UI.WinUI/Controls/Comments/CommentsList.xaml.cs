using System.Collections;
using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.ViewModels;
using Windows.Foundation;

namespace Wavee.UI.WinUI.Controls.Comments;

/// <summary>
/// Top-level comments orchestrator: optional header, composer, empty-state,
/// virtualized list of <see cref="CommentThreadItem"/>s, loading skeleton,
/// and load-more affordance. Drop-in replacement for the inline comments
/// blocks that previously lived in <c>YourEpisodesView</c> and the right
/// sidebar's Details mode. Composer + per-comment events bubble up so the
/// host can wire consent gates and reactions dialogs.
/// </summary>
public sealed partial class CommentsList : UserControl
{
    public static readonly DependencyProperty HeaderTextProperty =
        DependencyProperty.Register(
            nameof(HeaderText), typeof(string), typeof(CommentsList),
            new PropertyMetadata(null, OnHeaderTextChanged));

    public static readonly DependencyProperty CommentsProperty =
        DependencyProperty.Register(
            nameof(Comments), typeof(IEnumerable), typeof(CommentsList),
            new PropertyMetadata(null, OnCommentsChanged));

    public static readonly DependencyProperty CompactModeProperty =
        DependencyProperty.Register(
            nameof(CompactMode), typeof(bool), typeof(CommentsList),
            new PropertyMetadata(false));

    public static readonly DependencyProperty DraftProperty =
        DependencyProperty.Register(
            nameof(Draft), typeof(string), typeof(CommentsList),
            new PropertyMetadata("", OnDraftChanged));

    public static readonly DependencyProperty CharacterCountProperty =
        DependencyProperty.Register(
            nameof(CharacterCount), typeof(string), typeof(CommentsList),
            new PropertyMetadata("", OnCharacterCountChanged));

    public static readonly DependencyProperty StatusTextProperty =
        DependencyProperty.Register(
            nameof(StatusText), typeof(string), typeof(CommentsList),
            new PropertyMetadata(null, OnStatusTextChanged));

    public static readonly DependencyProperty SubmitCommandProperty =
        DependencyProperty.Register(
            nameof(SubmitCommand), typeof(ICommand), typeof(CommentsList),
            new PropertyMetadata(null, OnSubmitCommandChanged));

    public static readonly DependencyProperty IsSubmitEnabledProperty =
        DependencyProperty.Register(
            nameof(IsSubmitEnabled), typeof(bool), typeof(CommentsList),
            new PropertyMetadata(true, OnIsSubmitEnabledChanged));

    public static readonly DependencyProperty PlaceholderTextProperty =
        DependencyProperty.Register(
            nameof(PlaceholderText), typeof(string), typeof(CommentsList),
            new PropertyMetadata("Add a public comment...", OnPlaceholderChanged));

    public static readonly DependencyProperty SubmitButtonTextProperty =
        DependencyProperty.Register(
            nameof(SubmitButtonText), typeof(string), typeof(CommentsList),
            new PropertyMetadata("Comment", OnSubmitButtonTextChanged));

    public static readonly DependencyProperty ShowStatusAsInfoBarProperty =
        DependencyProperty.Register(
            nameof(ShowStatusAsInfoBar), typeof(bool), typeof(CommentsList),
            new PropertyMetadata(true, OnShowStatusAsInfoBarChanged));

    public static readonly DependencyProperty SubmitButtonStyleProperty =
        DependencyProperty.Register(
            nameof(SubmitButtonStyle), typeof(Style), typeof(CommentsList),
            new PropertyMetadata(null, OnSubmitButtonStyleChanged));

    public static readonly DependencyProperty HasNoCommentsProperty =
        DependencyProperty.Register(
            nameof(HasNoComments), typeof(bool), typeof(CommentsList),
            new PropertyMetadata(false, OnHasNoCommentsChanged));

    public static readonly DependencyProperty IsLoadingMoreProperty =
        DependencyProperty.Register(
            nameof(IsLoadingMore), typeof(bool), typeof(CommentsList),
            new PropertyMetadata(false, OnIsLoadingMoreChanged));

    public static readonly DependencyProperty HasMoreCommentsProperty =
        DependencyProperty.Register(
            nameof(HasMoreComments), typeof(bool), typeof(CommentsList),
            new PropertyMetadata(false, OnHasMoreCommentsChanged));

    public static readonly DependencyProperty LoadMoreCommandProperty =
        DependencyProperty.Register(
            nameof(LoadMoreCommand), typeof(ICommand), typeof(CommentsList),
            new PropertyMetadata(null, OnLoadMoreCommandChanged));

    public string? HeaderText
    {
        get => (string?)GetValue(HeaderTextProperty);
        set => SetValue(HeaderTextProperty, value);
    }

    public IEnumerable? Comments
    {
        get => (IEnumerable?)GetValue(CommentsProperty);
        set => SetValue(CommentsProperty, value);
    }

    public bool CompactMode
    {
        get => (bool)GetValue(CompactModeProperty);
        set => SetValue(CompactModeProperty, value);
    }

    public string Draft
    {
        get => (string)GetValue(DraftProperty);
        set => SetValue(DraftProperty, value);
    }

    public string CharacterCount
    {
        get => (string)GetValue(CharacterCountProperty);
        set => SetValue(CharacterCountProperty, value);
    }

    public string? StatusText
    {
        get => (string?)GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    public ICommand? SubmitCommand
    {
        get => (ICommand?)GetValue(SubmitCommandProperty);
        set => SetValue(SubmitCommandProperty, value);
    }

    public bool IsSubmitEnabled
    {
        get => (bool)GetValue(IsSubmitEnabledProperty);
        set => SetValue(IsSubmitEnabledProperty, value);
    }

    public string PlaceholderText
    {
        get => (string)GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    public string SubmitButtonText
    {
        get => (string)GetValue(SubmitButtonTextProperty);
        set => SetValue(SubmitButtonTextProperty, value);
    }

    public bool ShowStatusAsInfoBar
    {
        get => (bool)GetValue(ShowStatusAsInfoBarProperty);
        set => SetValue(ShowStatusAsInfoBarProperty, value);
    }

    public Style? SubmitButtonStyle
    {
        get => (Style?)GetValue(SubmitButtonStyleProperty);
        set => SetValue(SubmitButtonStyleProperty, value);
    }

    public bool HasNoComments
    {
        get => (bool)GetValue(HasNoCommentsProperty);
        set => SetValue(HasNoCommentsProperty, value);
    }

    public bool IsLoadingMore
    {
        get => (bool)GetValue(IsLoadingMoreProperty);
        set => SetValue(IsLoadingMoreProperty, value);
    }

    public bool HasMoreComments
    {
        get => (bool)GetValue(HasMoreCommentsProperty);
        set => SetValue(HasMoreCommentsProperty, value);
    }

    public ICommand? LoadMoreCommand
    {
        get => (ICommand?)GetValue(LoadMoreCommandProperty);
        set => SetValue(LoadMoreCommandProperty, value);
    }

    /// <summary>Fired when the user clicks the composer Submit button.</summary>
    public event TypedEventHandler<CommentsList, RoutedEventArgs>? SubmitRequested;
    /// <summary>Fired when the user taps a top-level comment's reactions chip.</summary>
    public event TypedEventHandler<CommentsList, PodcastCommentViewModel>? ShowReactionsRequested;
    /// <summary>Fired when the user taps a nested reply's reactions chip.</summary>
    public event TypedEventHandler<CommentsList, PodcastReplyViewModel>? ShowReplyReactionsRequested;
    /// <summary>Fired when the user taps the per-comment "Reply" submit button.</summary>
    public event TypedEventHandler<CommentsList, PodcastCommentViewModel>? ReplySubmitRequested;

    public CommentsList()
    {
        InitializeComponent();
        DraftBox_Initialise();
        Loaded += (_, _) => ApplyAll();
    }

    private void DraftBox_Initialise()
    {
        // Forward composer Draft changes back into our DP. Two-way bridge so
        // the host's TwoWay binding still picks up edits.
        ComposerControl.RegisterPropertyChangedCallback(CommentComposer.DraftProperty, OnComposerDraftChanged);
    }

    private void OnComposerDraftChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (Draft != ComposerControl.Draft) Draft = ComposerControl.Draft;
    }

    private void ApplyAll()
    {
        ApplyHeader();
        ApplyComments();
        ApplyComposerForward();
        ApplyEmptyState();
        ApplyLoadingState();
        ApplyLoadMoreState();
    }

    private void ApplyHeader()
    {
        var hasHeader = !string.IsNullOrEmpty(HeaderText);
        HeaderPanel.Visibility = hasHeader ? Visibility.Visible : Visibility.Collapsed;
        HeaderTextBlock.Text = HeaderText ?? "";
    }

    private void ApplyComments()
    {
        CommentsRepeater.ItemsSource = Comments;
    }

    private void ApplyComposerForward()
    {
        if (ComposerControl.Draft != (Draft ?? "")) ComposerControl.Draft = Draft ?? "";
        ComposerControl.CharacterCount = CharacterCount ?? "";
        ComposerControl.StatusText = StatusText;
        ComposerControl.SubmitCommand = SubmitCommand;
        ComposerControl.IsSubmitEnabled = IsSubmitEnabled;
        ComposerControl.PlaceholderText = PlaceholderText ?? "";
        ComposerControl.SubmitButtonText = SubmitButtonText ?? "";
        ComposerControl.ShowStatusAsInfoBar = ShowStatusAsInfoBar;
        if (SubmitButtonStyle is not null) ComposerControl.SubmitButtonStyle = SubmitButtonStyle;
    }

    private void ApplyEmptyState()
    {
        EmptyHintPanel.Visibility = HasNoComments ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyLoadingState()
    {
        LoadingPanel.Visibility = IsLoadingMore ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyLoadMoreState()
    {
        LoadMoreButton.Visibility = HasMoreComments ? Visibility.Visible : Visibility.Collapsed;
        LoadMoreButton.Command = LoadMoreCommand;
    }

    // ── DP change callbacks ──────────────────────────────────────────────

    private static void OnHeaderTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CommentsList c) c.ApplyHeader();
    }

    private static void OnCommentsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CommentsList c) c.ApplyComments();
    }

    private static void OnDraftChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CommentsList c && c.ComposerControl.Draft != (e.NewValue as string ?? ""))
            c.ComposerControl.Draft = e.NewValue as string ?? "";
    }

    private static void OnCharacterCountChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CommentsList c) c.ComposerControl.CharacterCount = e.NewValue as string ?? "";
    }

    private static void OnStatusTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CommentsList c) c.ComposerControl.StatusText = e.NewValue as string;
    }

    private static void OnSubmitCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CommentsList c) c.ComposerControl.SubmitCommand = e.NewValue as ICommand;
    }

    private static void OnIsSubmitEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CommentsList c) c.ComposerControl.IsSubmitEnabled = (bool)e.NewValue;
    }

    private static void OnPlaceholderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CommentsList c) c.ComposerControl.PlaceholderText = e.NewValue as string ?? "";
    }

    private static void OnSubmitButtonTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CommentsList c) c.ComposerControl.SubmitButtonText = e.NewValue as string ?? "";
    }

    private static void OnShowStatusAsInfoBarChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CommentsList c) c.ComposerControl.ShowStatusAsInfoBar = (bool)e.NewValue;
    }

    private static void OnSubmitButtonStyleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CommentsList c && e.NewValue is Style s) c.ComposerControl.SubmitButtonStyle = s;
    }

    private static void OnHasNoCommentsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CommentsList c) c.ApplyEmptyState();
    }

    private static void OnIsLoadingMoreChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CommentsList c) c.ApplyLoadingState();
    }

    private static void OnHasMoreCommentsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CommentsList c) c.ApplyLoadMoreState();
    }

    private static void OnLoadMoreCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CommentsList c) c.LoadMoreButton.Command = e.NewValue as ICommand;
    }

    // ── Event bubbling ───────────────────────────────────────────────────

    private void Composer_SubmitRequested(CommentComposer sender, RoutedEventArgs args)
    {
        SubmitRequested?.Invoke(this, args);
    }

    private void Item_ShowReactionsRequested(CommentThreadItem sender, PodcastCommentViewModel comment)
    {
        ShowReactionsRequested?.Invoke(this, comment);
    }

    private void Item_ShowReplyReactionsRequested(CommentThreadItem sender, PodcastReplyViewModel reply)
    {
        ShowReplyReactionsRequested?.Invoke(this, reply);
    }

    private void Item_ReplySubmitRequested(CommentThreadItem sender, PodcastCommentViewModel comment)
    {
        ReplySubmitRequested?.Invoke(this, comment);
    }
}
