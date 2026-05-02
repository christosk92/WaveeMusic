using System;
using System.Collections.Generic;
using System.Linq;

namespace Wavee.UI.WinUI.Data.DTOs;

public sealed record PodcastEpisodeDetailDto
{
    public required string Uri { get; init; }
    public required string Title { get; init; }
    public string? ShowUri { get; init; }
    public string? ShowName { get; init; }
    public string? ImageUrl { get; init; }
    public string? ShowImageUrl { get; init; }
    public string? Description { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTimeOffset? ReleaseDate { get; init; }
    public DateTime AddedAt { get; init; }
    public bool IsExplicit { get; init; }
    public bool IsPlayable { get; init; } = true;
    public bool IsPaywalled { get; init; }
    public string? ShareUrl { get; init; }
    public string? PreviewUrl { get; init; }
    public string? PlayedState { get; init; }
    public TimeSpan PlayedPosition { get; init; }
    public IReadOnlyList<string> MediaTypes { get; init; } = [];
    public IReadOnlyList<string> TranscriptLanguages { get; init; } = [];
    public IReadOnlyList<PodcastEpisodeRecommendationDto> Recommendations { get; init; } = [];
    public IReadOnlyList<PodcastEpisodeCommentDto> Comments { get; init; } = [];
    public string? CommentsNextPageToken { get; init; }
    public int CommentsTotalCount { get; init; }

    public string DurationFormatted => Duration.TotalHours >= 1
        ? Duration.ToString(@"h\:mm\:ss")
        : Duration.ToString(@"m\:ss");

    public string ReleaseDateFormatted => ReleaseDate is DateTimeOffset release
        ? release.LocalDateTime.ToString("MMM d, yyyy")
        : "";

    public string AddedAtFormatted => FormatRelativeDate(AddedAt);

    public string Metadata
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(ShowName))
                parts.Add(ShowName!);
            if (!string.IsNullOrWhiteSpace(ReleaseDateFormatted))
                parts.Add(ReleaseDateFormatted);
            if (Duration > TimeSpan.Zero)
                parts.Add(DurationFormatted);
            if (IsExplicit)
                parts.Add("Explicit");
            return string.Join(" - ", parts);
        }
    }

    public string Availability
    {
        get
        {
            if (IsPaywalled)
                return "Paywalled";
            if (!IsPlayable)
                return "Unavailable";
            if (!string.IsNullOrWhiteSpace(PreviewUrl))
                return "Preview available";
            return "Playable";
        }
    }

    public string TranscriptSummary => TranscriptLanguages.Count == 0
        ? "No transcript"
        : string.Join(", ", TranscriptLanguages.Distinct(StringComparer.OrdinalIgnoreCase));

    public static PodcastEpisodeDetailDto FromEpisode(LibraryEpisodeDto episode)
    {
        ArgumentNullException.ThrowIfNull(episode);

        return new PodcastEpisodeDetailDto
        {
            Uri = episode.Uri,
            Title = episode.Title,
            ShowUri = episode.AlbumId,
            ShowName = episode.AlbumName,
            ImageUrl = episode.ImageUrl,
            ShowImageUrl = episode.ImageUrl,
            Description = episode.Description,
            Duration = episode.Duration,
            ReleaseDate = episode.ReleaseDate,
            AddedAt = episode.AddedAt,
            IsExplicit = episode.IsExplicit,
            IsPlayable = episode.IsPlayable,
            ShareUrl = episode.ShareUrl,
            PreviewUrl = episode.PreviewUrl,
            PlayedState = episode.PlayedState,
            PlayedPosition = episode.PlayedPosition,
            MediaTypes = episode.MediaTypes
        };
    }

    private static string FormatRelativeDate(DateTime date)
    {
        var diff = DateTime.Now - date;
        if (diff.TotalDays < 1) return "Today";
        if (diff.TotalDays < 2) return "Yesterday";
        if (diff.TotalDays < 7) return $"{(int)diff.TotalDays} days ago";
        if (diff.TotalDays < 30) return $"{(int)(diff.TotalDays / 7)} weeks ago";
        if (diff.TotalDays < 365) return $"{(int)(diff.TotalDays / 30)} months ago";
        return date.ToString("MMM d, yyyy");
    }
}

public sealed record PodcastEpisodeRecommendationDto
{
    public required string Uri { get; init; }
    public required string Title { get; init; }
    public string? ShowName { get; init; }
    public string? ImageUrl { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTimeOffset? ReleaseDate { get; init; }
    public bool IsExplicit { get; init; }

    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(ShowName))
                parts.Add(ShowName!);
            if (ReleaseDate is DateTimeOffset release)
                parts.Add(release.LocalDateTime.ToString("MMM d, yyyy"));
            if (Duration > TimeSpan.Zero)
                parts.Add(Duration.TotalHours >= 1 ? Duration.ToString(@"h\:mm\:ss") : Duration.ToString(@"m\:ss"));
            return string.Join(" - ", parts);
        }
    }
}

public sealed record PodcastEpisodeCommentDto
{
    public required string Uri { get; init; }
    public required string Text { get; init; }
    public string? AuthorName { get; init; }
    public string? AuthorImageUrl { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public int ReactionCount { get; init; }
    public int ReplyCount { get; init; }
    public bool IsPinned { get; init; }
    public string? UserReactionEmoji { get; init; }
    public IReadOnlyList<string> TopReactionEmoji { get; init; } = [];
    public IReadOnlyList<PodcastCommentAvatarDto> TopReplyAuthors { get; init; } = [];

    public string TopReactionEmojiString => string.Concat(TopReactionEmoji);

    public string CreatedAtFormatted => CreatedAt is DateTimeOffset created
        ? FormatRelativeTime(created)
        : "";

    public string ReactionsLabel => ReactionCount switch
    {
        <= 0 => "",
        1 => "1 reaction",
        _ => $"{ReactionCount:N0} reactions"
    };

    public string RepliesLabel => ReplyCount switch
    {
        <= 0 => "",
        1 => "1 reply",
        _ => $"{ReplyCount:N0} replies"
    };

    public bool HasReactions => ReactionCount > 0;
    public bool HasReplies => ReplyCount > 0;

    public string Metadata
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(AuthorName))
                parts.Add(AuthorName!);
            if (CreatedAt is DateTimeOffset created)
                parts.Add(created.LocalDateTime.ToString("MMM d"));
            if (ReactionCount > 0)
                parts.Add(ReactionCount == 1 ? "1 reaction" : $"{ReactionCount} reactions");
            if (ReplyCount > 0)
                parts.Add(ReplyCount == 1 ? "1 reply" : $"{ReplyCount} replies");
            return string.Join(" - ", parts);
        }
    }

    private static string FormatRelativeTime(DateTimeOffset created)
    {
        var diff = DateTimeOffset.Now - created;
        if (diff.TotalSeconds < 60) return "just now";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes} min ago";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours} hr ago";
        if (diff.TotalDays < 7) return $"{(int)diff.TotalDays} d ago";
        if (diff.TotalDays < 30) return $"{(int)(diff.TotalDays / 7)} wk ago";
        if (diff.TotalDays < 365) return created.LocalDateTime.ToString("MMM d");
        return created.LocalDateTime.ToString("MMM d, yyyy");
    }
}

public sealed record PodcastCommentAvatarDto
{
    public required string Name { get; init; }
    public string? ImageUrl { get; init; }
}

public sealed record PodcastEpisodeCommentReplyDto
{
    public required string Uri { get; init; }
    public required string Text { get; init; }
    public string? AuthorName { get; init; }
    public string? AuthorImageUrl { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public int ReactionCount { get; init; }
    public string? UserReactionEmoji { get; init; }
    public IReadOnlyList<string> TopReactionEmoji { get; init; } = [];

    public string TopReactionEmojiString => string.Concat(TopReactionEmoji);

    public string CreatedAtFormatted
    {
        get
        {
            if (CreatedAt is not DateTimeOffset created) return "";
            var diff = DateTimeOffset.Now - created;
            if (diff.TotalSeconds < 60) return "just now";
            if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes} min ago";
            if (diff.TotalHours < 24) return $"{(int)diff.TotalHours} hr ago";
            if (diff.TotalDays < 7) return $"{(int)diff.TotalDays} d ago";
            if (diff.TotalDays < 30) return $"{(int)(diff.TotalDays / 7)} wk ago";
            if (diff.TotalDays < 365) return created.LocalDateTime.ToString("MMM d");
            return created.LocalDateTime.ToString("MMM d, yyyy");
        }
    }

    public bool HasReactions => ReactionCount > 0;
    public string ReactionsLabel => ReactionCount switch
    {
        <= 0 => "",
        1 => "1",
        _ => ReactionCount.ToString("N0")
    };
}

public sealed record PodcastCommentRepliesPageDto
{
    public required IReadOnlyList<PodcastEpisodeCommentReplyDto> Items { get; init; }
    public string? NextPageToken { get; init; }
    public int TotalCount { get; init; }
}

public sealed record PodcastCommentReactionDto
{
    public string? AuthorName { get; init; }
    public string? AuthorImageUrl { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public required string ReactionUnicode { get; init; }

    public string CreatedAtFormatted
    {
        get
        {
            if (CreatedAt is not DateTimeOffset created) return "";
            var diff = DateTimeOffset.Now - created;
            if (diff.TotalSeconds < 60) return "just now";
            if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes} min ago";
            if (diff.TotalHours < 24) return $"{(int)diff.TotalHours} hr ago";
            if (diff.TotalDays < 7) return $"{(int)diff.TotalDays} d ago";
            if (diff.TotalDays < 30) return $"{(int)(diff.TotalDays / 7)} wk ago";
            if (diff.TotalDays < 365) return created.LocalDateTime.ToString("MMM d");
            return created.LocalDateTime.ToString("MMM d, yyyy");
        }
    }
}

public sealed record PodcastCommentReactionCountDto
{
    public required string ReactionUnicode { get; init; }
    public int Count { get; init; }
    public string CountFormatted => Count.ToString("N0");
}

public sealed record PodcastCommentReactionsPageDto
{
    public required IReadOnlyList<PodcastCommentReactionDto> Items { get; init; }
    public required IReadOnlyList<PodcastCommentReactionCountDto> ReactionCounts { get; init; }
    public string? NextPageToken { get; init; }
}

public sealed record PodcastEpisodeCommentsPageDto
{
    public required IReadOnlyList<PodcastEpisodeCommentDto> Items { get; init; }
    public string? NextPageToken { get; init; }
    public int TotalCount { get; init; }
}

public sealed record PodcastEpisodeProgressDto
{
    public const string ErrorState = "PROGRESS_ERROR";

    public required string Uri { get; init; }
    public TimeSpan PlayedPosition { get; init; }
    public string? PlayedState { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}
