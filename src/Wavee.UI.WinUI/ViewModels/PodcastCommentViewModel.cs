using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// Per-comment wrapper that owns its expand/collapse + reply pagination state
/// so the templated comment row can drive its own UI without round-tripping
/// through the parent <see cref="YourEpisodesViewModel"/>.
/// </summary>
public sealed partial class PodcastCommentViewModel : ObservableObject
{
    private const int MaxPodcastReplyLength = 500;
    private const int MaxVisibleReactionEmoji = 3;
    private static readonly string[] SupportedReactionEmoji =
    [
        "\u2764\uFE0F",
        "\U0001F602",
        "\U0001F62E",
        "\U0001F44E",
        "\U0001F44F",
        "\U0001F525",
        "\U0001F44D"
    ];
    private readonly ILibraryDataService _libraryDataService;
    private readonly IPodcastEpisodeService _podcastEpisodeService;
    private readonly ILogger? _logger;
    private readonly List<string> _topReactionEmoji;
    private int _submittedReplyCount;
    private int _reactionCount;
    private string? _selectedReactionEmoji;

    public PodcastEpisodeCommentDto Data { get; }

    public ObservableCollection<PodcastReplyViewModel> Replies { get; } = [];
    public ObservableCollection<StackedAvatarItem> TopReplyAvatarItems { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RepliesEmpty))]
    private bool _isRepliesExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RepliesEmpty))]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreRepliesCommand))]
    private bool _isLoadingReplies;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMoreReplies))]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreRepliesCommand))]
    private string? _repliesNextPageToken;

    [ObservableProperty]
    private bool _isReplyComposerOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReplyCharacterCount))]
    [NotifyPropertyChangedFor(nameof(CanSubmitReply))]
    [NotifyCanExecuteChangedFor(nameof(SubmitReplyCommand))]
    private string _replyDraft = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmitReply))]
    [NotifyCanExecuteChangedFor(nameof(SubmitReplyCommand))]
    private bool _isSubmittingReply;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReplyComposerStatus))]
    private string? _replyComposerStatus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReact))]
    [NotifyCanExecuteChangedFor(nameof(ReactCommand))]
    private bool _isReacting;

    public PodcastCommentViewModel(
        PodcastEpisodeCommentDto data,
        ILibraryDataService libraryDataService,
        IPodcastEpisodeService podcastEpisodeService,
        ILogger? logger = null)
    {
        Data = data;
        _libraryDataService = libraryDataService;
        _podcastEpisodeService = podcastEpisodeService;
        _logger = logger;
        _topReactionEmoji = data.TopReactionEmoji
            .Where(static emoji => !string.IsNullOrWhiteSpace(emoji))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxVisibleReactionEmoji)
            .ToList();
        _reactionCount = data.ReactionCount;
        _selectedReactionEmoji = data.UserReactionEmoji;
        RepliesNextPageToken = data.HasReplies ? "" : null;

        foreach (var avatar in data.TopReplyAuthors)
        {
            TopReplyAvatarItems.Add(new StackedAvatarItem
            {
                DisplayName = avatar.Name,
                AvatarUrl = avatar.ImageUrl
            });
        }
    }

    public bool HasMoreReplies => RepliesNextPageToken is not null;
    public IReadOnlyList<string> ReactionOptions => SupportedReactionEmoji;
    public bool HasReactions => _reactionCount > 0;
    public bool HasReplies => DisplayReplyCount > 0;
    public bool HasTopReplyAvatars => TopReplyAvatarItems.Count > 0;
    public string TopReactionEmojiString => string.Concat(_topReactionEmoji);
    public string ReactionsLabel => _reactionCount switch
    {
        <= 0 => "",
        1 => "1 reaction",
        _ => $"{_reactionCount:N0} reactions"
    };
    public string RepliesLabel => DisplayReplyCount switch
    {
        <= 0 => "",
        1 => "1 reply",
        _ => $"{DisplayReplyCount:N0} replies"
    };
    public bool RepliesEmpty => IsRepliesExpanded && !IsLoadingReplies && Replies.Count == 0 && !HasMoreReplies;
    public string ReplyCharacterCount => $"{Math.Min(ReplyDraft.Length, MaxPodcastReplyLength)}/{MaxPodcastReplyLength}";
    public string ReplyPlaceholder => string.IsNullOrWhiteSpace(Data.AuthorName)
        ? "Write a reply..."
        : $"Reply to {Data.AuthorName}...";
    public bool CanSubmitReply => !IsSubmittingReply
        && NormalizeReplyText(ReplyDraft) is { Length: > 0 and <= MaxPodcastReplyLength };
    public bool HasReplyComposerStatus => !string.IsNullOrWhiteSpace(ReplyComposerStatus);
    public bool CanReact => !IsReacting;

    private int DisplayReplyCount => Math.Max(Data.ReplyCount + _submittedReplyCount, Replies.Count);

    partial void OnIsRepliesExpandedChanged(bool value)
    {
        if (value && Replies.Count == 0 && HasMoreReplies && !IsLoadingReplies)
            _ = LoadMoreRepliesInternalAsync();
    }

    [RelayCommand]
    private async Task ToggleRepliesAsync()
    {
        if (IsRepliesExpanded)
        {
            IsRepliesExpanded = false;
            return;
        }

        IsRepliesExpanded = true;
        if (Replies.Count == 0 && HasMoreReplies)
        {
            await LoadMoreRepliesInternalAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanLoadMoreReplies))]
    private Task LoadMoreRepliesAsync() => LoadMoreRepliesInternalAsync();

    private bool CanLoadMoreReplies() => HasMoreReplies && !IsLoadingReplies;

    [RelayCommand]
    private void ShowReplyComposer()
    {
        IsReplyComposerOpen = true;
        ReplyComposerStatus = null;
    }

    [RelayCommand]
    private void CancelReply()
    {
        IsReplyComposerOpen = false;
        ReplyDraft = "";
        ReplyComposerStatus = null;
    }

    [RelayCommand(CanExecute = nameof(CanSubmitReply))]
    private async Task SubmitReplyAsync()
    {
        var text = NormalizeReplyText(ReplyDraft);
        if (text.Length == 0 || text.Length > MaxPodcastReplyLength)
            return;

        IsSubmittingReply = true;
        ReplyComposerStatus = null;
        try
        {
            var reply = await _podcastEpisodeService .CreatePodcastCommentReplyAsync(Data.Uri, text)
                .ConfigureAwait(true);

            Replies.Insert(0, new PodcastReplyViewModel(reply, _libraryDataService, _podcastEpisodeService, _logger));
            _submittedReplyCount++;
            ReplyDraft = "";
            IsReplyComposerOpen = false;
            IsRepliesExpanded = true;
            ReplyComposerStatus = "Reply saved locally. Spotify posting is not wired yet.";
            OnPropertyChanged(nameof(HasReplies));
            OnPropertyChanged(nameof(RepliesLabel));
            OnPropertyChanged(nameof(RepliesEmpty));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to add local podcast comment reply for {CommentUri}", Data.Uri);
            ReplyComposerStatus = "Could not add the reply.";
        }
        finally
        {
            IsSubmittingReply = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanReact))]
    private async Task ReactAsync(string emoji)
    {
        emoji = NormalizeReactionEmoji(emoji);
        if (emoji.Length == 0)
            return;

        IsReacting = true;
        try
        {
            await _podcastEpisodeService .ReactToPodcastCommentAsync(Data.Uri, emoji)
                .ConfigureAwait(true);

            ApplyReaction(emoji);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to react to podcast comment {CommentUri}", Data.Uri);
        }
        finally
        {
            IsReacting = false;
        }
    }

    public Task<PodcastCommentReactionsPageDto?> GetReactionsAsync(
        string? pageToken = null,
        string? reactionUnicode = null,
        CancellationToken ct = default)
        => _podcastEpisodeService.GetPodcastCommentReactionsAsync(Data.Uri, pageToken, reactionUnicode, ct);

    private async Task LoadMoreRepliesInternalAsync()
    {
        if (IsLoadingReplies || !HasMoreReplies) return;
        IsLoadingReplies = true;
        try
        {
            // The first call uses an empty token; subsequent calls use the real
            // continuation token returned by Spotify. Either way, pass null when
            // empty so the protocol-side variable serializes as `pageToken: null`.
            var token = string.IsNullOrEmpty(RepliesNextPageToken) ? null : RepliesNextPageToken;
            var page = await _podcastEpisodeService.GetPodcastCommentRepliesAsync(Data.Uri, token, CancellationToken.None);
            if (page is null)
            {
                RepliesNextPageToken = null;
                return;
            }

            foreach (var reply in page.Items)
                Replies.Add(new PodcastReplyViewModel(reply, _libraryDataService, _podcastEpisodeService, _logger));

            RepliesNextPageToken = page.NextPageToken;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load comment replies for {Uri}", Data.Uri);
            RepliesNextPageToken = null;
        }
        finally
        {
            IsLoadingReplies = false;
        }
    }

    private static string NormalizeReplyText(string? value)
        => string.IsNullOrWhiteSpace(value) ? "" : value.Trim();

    private void ApplyReaction(string emoji)
    {
        if (_selectedReactionEmoji is null)
            _reactionCount++;
        else
            _topReactionEmoji.Remove(_selectedReactionEmoji);

        _selectedReactionEmoji = emoji;
        _topReactionEmoji.Remove(emoji);
        _topReactionEmoji.Insert(0, emoji);
        while (_topReactionEmoji.Count > MaxVisibleReactionEmoji)
            _topReactionEmoji.RemoveAt(_topReactionEmoji.Count - 1);

        OnPropertyChanged(nameof(HasReactions));
        OnPropertyChanged(nameof(TopReactionEmojiString));
        OnPropertyChanged(nameof(ReactionsLabel));
    }

    private static string NormalizeReactionEmoji(string? value)
        => string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
}

public sealed partial class PodcastReplyViewModel : ObservableObject
{
    private const int MaxVisibleReactionEmoji = 3;
    private static readonly string[] SupportedReactionEmoji =
    [
        "\u2764\uFE0F",
        "\U0001F602",
        "\U0001F62E",
        "\U0001F44E",
        "\U0001F44F",
        "\U0001F525",
        "\U0001F44D"
    ];
    private readonly ILibraryDataService _libraryDataService;
    private readonly IPodcastEpisodeService _podcastEpisodeService;
    private readonly ILogger? _logger;
    private readonly List<string> _topReactionEmoji;
    private int _reactionCount;
    private string? _selectedReactionEmoji;

    public PodcastReplyViewModel(
        PodcastEpisodeCommentReplyDto data,
        ILibraryDataService libraryDataService,
        IPodcastEpisodeService podcastEpisodeService,
        ILogger? logger = null)
    {
        _podcastEpisodeService = podcastEpisodeService;
        Data = data;
        _libraryDataService = libraryDataService;
        _logger = logger;
        _topReactionEmoji = data.TopReactionEmoji
            .Where(static emoji => !string.IsNullOrWhiteSpace(emoji))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxVisibleReactionEmoji)
            .ToList();
        _reactionCount = data.ReactionCount;
        _selectedReactionEmoji = data.UserReactionEmoji;
    }

    public PodcastEpisodeCommentReplyDto Data { get; }
    public IReadOnlyList<string> ReactionOptions => SupportedReactionEmoji;
    public bool HasReactions => _reactionCount > 0;
    public string TopReactionEmojiString => string.Concat(_topReactionEmoji);
    public string ReactionsLabel => _reactionCount switch
    {
        <= 0 => "",
        _ => _reactionCount.ToString("N0")
    };
    public bool CanReact => !IsReacting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReact))]
    [NotifyCanExecuteChangedFor(nameof(ReactCommand))]
    private bool _isReacting;

    [RelayCommand(CanExecute = nameof(CanReact))]
    private async Task ReactAsync(string emoji)
    {
        emoji = NormalizeReactionEmoji(emoji);
        if (emoji.Length == 0)
            return;

        IsReacting = true;
        try
        {
            await _podcastEpisodeService .ReactToPodcastCommentReplyAsync(Data.Uri, emoji)
                .ConfigureAwait(true);

            ApplyReaction(emoji);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to react to podcast comment reply {ReplyUri}", Data.Uri);
        }
        finally
        {
            IsReacting = false;
        }
    }

    public Task<PodcastCommentReactionsPageDto?> GetReactionsAsync(
        string? pageToken = null,
        string? reactionUnicode = null,
        CancellationToken ct = default)
        => _podcastEpisodeService.GetPodcastCommentReactionsAsync(Data.Uri, pageToken, reactionUnicode, ct);

    private void ApplyReaction(string emoji)
    {
        if (_selectedReactionEmoji is null)
            _reactionCount++;
        else
            _topReactionEmoji.Remove(_selectedReactionEmoji);

        _selectedReactionEmoji = emoji;
        _topReactionEmoji.Remove(emoji);
        _topReactionEmoji.Insert(0, emoji);
        while (_topReactionEmoji.Count > MaxVisibleReactionEmoji)
            _topReactionEmoji.RemoveAt(_topReactionEmoji.Count - 1);

        OnPropertyChanged(nameof(HasReactions));
        OnPropertyChanged(nameof(TopReactionEmojiString));
        OnPropertyChanged(nameof(ReactionsLabel));
    }

    private static string NormalizeReactionEmoji(string? value)
        => string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
}
