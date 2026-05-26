using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.AI.Activity;
using Wavee.AI.Generation;
using Wavee.AI.Tools;

namespace Wavee.AI.Artists;

public sealed record ArtistAiQuestionRequest(
    string ArtistUri,
    string ArtistName,
    string Question,
    string? Biography = null,
    IReadOnlyList<string>? KnownTopTrackNames = null,
    IProgress<string>? DeltaProgress = null,
    IAiActivitySink? ActivitySink = null);

public sealed record ArtistAiSuggestionRequest(
    string ArtistUri,
    string ArtistName,
    string? Biography = null,
    IReadOnlyList<string>? KnownTopTrackNames = null);

public enum ArtistAiQuestionResultKind
{
    Ok,
    Empty,
    Unavailable,
    Filtered,
    Error,
}

public sealed record ArtistAiQuestionResult(
    ArtistAiQuestionResultKind Kind,
    string Text,
    string? ErrorMessage = null,
    IReadOnlyList<ArtistAiRecommendation>? Recommendations = null)
{
    public static readonly ArtistAiQuestionResult Empty =
        new(ArtistAiQuestionResultKind.Empty, string.Empty);

    public static ArtistAiQuestionResult Ok(
        string text,
        IReadOnlyList<ArtistAiRecommendation>? recommendations = null)
        => new(ArtistAiQuestionResultKind.Ok, text, Recommendations: recommendations);

    public static ArtistAiQuestionResult Unavailable(string reason)
        => new(ArtistAiQuestionResultKind.Unavailable, string.Empty, reason);

    public static ArtistAiQuestionResult Filtered(string reason)
        => new(ArtistAiQuestionResultKind.Filtered, string.Empty, reason);

    public static ArtistAiQuestionResult Error(string reason)
        => new(ArtistAiQuestionResultKind.Error, string.Empty, reason);
}

public enum ArtistAiRecommendationKind
{
    Track,
    Release,
}

public sealed record ArtistAiRecommendation(
    ArtistAiRecommendationKind Kind,
    string Title,
    string? Subtitle,
    string? Uri,
    string? ImageUrl = null,
    string? ContextUri = null,
    string? Reason = null,
    long? PlayCount = null,
    int? Year = null);

public sealed record ArtistProfileFacts(
    string ArtistUri,
    string? Name,
    string? Biography,
    long MonthlyListeners,
    long Followers,
    int? WorldRank,
    IReadOnlyList<string> TopCities);

public sealed record ArtistTrackFact(
    string? Title,
    string? Uri,
    string? AlbumName,
    string? AlbumUri,
    string? ImageUrl,
    long PlayCount,
    int? Year,
    DateTimeOffset ReleaseDate = default,
    int TrackNumber = 0);

public sealed record ArtistReleaseFact(
    string? Name,
    string? Uri,
    string Type,
    string? ImageUrl,
    DateTimeOffset ReleaseDate,
    int TrackCount,
    string? Label,
    int Year);

public interface IArtistAiToolProvider
{
    Task<ArtistProfileFacts> GetProfileAsync(
        string artistUri,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ArtistTrackFact>> GetTopTracksAsync(
        string artistUri,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ArtistReleaseFact>> GetDiscographyAsync(
        string artistUri,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ArtistTrackFact>> GetReleaseTracksAsync(
        string artistUri,
        IReadOnlyList<ArtistReleaseFact> releases,
        int maxReleases = 24,
        CancellationToken cancellationToken = default);
}

public sealed class ArtistAiQuestionService
{
    private const int MaxPromptCharacters = 8000;

    private readonly ILanguageModelClient _model;
    private readonly IArtistAiToolProvider _artistTools;
    private readonly IAiFeatureSettings _settings;
    private readonly IWebSearchToolProvider? _webSearch;
    private readonly IWikipediaLookup? _wikipedia;
    private readonly ILogger? _logger;

    public ArtistAiQuestionService(
        ILanguageModelClient model,
        IArtistAiToolProvider artistTools,
        IAiFeatureSettings settings,
        IWebSearchToolProvider? webSearch = null,
        IWikipediaLookup? wikipedia = null,
        ILogger<ArtistAiQuestionService>? logger = null)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _artistTools = artistTools ?? throw new ArgumentNullException(nameof(artistTools));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _webSearch = webSearch;
        _wikipedia = wikipedia;
        _logger = logger;
    }

    public async Task<ArtistAiQuestionResult> AskAsync(
        ArtistAiQuestionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var activity = request.ActivitySink ?? NullAiActivitySink.Instance;
        if (string.IsNullOrWhiteSpace(request.Question))
            return ArtistAiQuestionResult.Empty;
        if (string.IsNullOrWhiteSpace(request.ArtistUri) || string.IsNullOrWhiteSpace(request.ArtistName))
            return ArtistAiQuestionResult.Unavailable("There is not enough artist context yet.");
        if (!_settings.AiFeaturesEnabled || !_settings.AiBioSummarizeEnabled)
            return ArtistAiQuestionResult.Unavailable("Artist AI is disabled in settings.");
        if (!_model.IsSupported)
            return ArtistAiQuestionResult.Unavailable(_model.DescribeStatus());

        activity.Report(new AiActivityEvent(
            AiActivityKind.Started,
            "Understanding your artist question",
            Detail: request.Question.Trim()));

        if (!await _model.EnsureReadyAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            return ArtistAiQuestionResult.Unavailable(_model.DescribeStatus());

        var plan = await BuildPlanAsync(request, activity, cancellationToken).ConfigureAwait(false);
        var context = await LoadContextAsync(request, plan, activity, cancellationToken).ConfigureAwait(false);
        var recommendations = BuildRecommendations(plan, context);
        if (recommendations.Count > 0)
        {
            activity.Report(new AiActivityEvent(
                AiActivityKind.ToolCompleted,
                $"Prepared {recommendations.Count} catalog item{(recommendations.Count == 1 ? "" : "s")}",
                ToolName: "artist.recommendations"));
        }

        activity.Report(new AiActivityEvent(
            AiActivityKind.ModelStarted,
            "Writing the answer with Phi Silica"));

        var prompt = BuildAnswerPrompt(request, plan, context);
        var response = await _model.GenerateTextAsync(
            new AiTextGenerationRequest(prompt, TemperatureFor(plan), "ArtistQuestionAnswer"),
            request.DeltaProgress,
            cancellationToken).ConfigureAwait(false);

        activity.Report(new AiActivityEvent(
            response.IsComplete ? AiActivityKind.ModelCompleted : AiActivityKind.Warning,
            response.IsComplete ? "Finished answer" : $"Model returned {response.Status}",
            Detail: response.ErrorMessage));

        return response.Status switch
        {
            AiGenerationStatus.Complete when !string.IsNullOrWhiteSpace(response.Text) =>
                ArtistAiQuestionResult.Ok(CleanAnswer(response.Text), recommendations),
            AiGenerationStatus.BlockedByPolicy or
            AiGenerationStatus.PromptBlockedByContentModeration or
            AiGenerationStatus.ResponseBlockedByContentModeration =>
                ArtistAiQuestionResult.Filtered("The on-device model blocked this answer."),
            AiGenerationStatus.Unavailable =>
                ArtistAiQuestionResult.Unavailable(response.ErrorMessage ?? _model.DescribeStatus()),
            _ when recommendations.Count > 0 =>
                ArtistAiQuestionResult.Ok(BuildFallbackAnswer(plan, recommendations), recommendations),
            _ => ArtistAiQuestionResult.Error(response.ErrorMessage ?? response.Status.ToString()),
        };
    }

    public async Task<IReadOnlyList<string>> SuggestQuestionsAsync(
        ArtistAiSuggestionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ArtistUri) || string.IsNullOrWhiteSpace(request.ArtistName))
            return [];
        if (!_settings.AiFeaturesEnabled || !_settings.AiBioSummarizeEnabled || !_model.IsSupported)
            return [];

        try
        {
            if (!await _model.EnsureReadyAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
                return [];

            var response = await _model.GenerateStructuredJsonAsync(
                new AiStructuredGenerationRequest(
                    BuildSuggestionPrompt(request),
                    SuggestionJsonSchema,
                    Temperature: 0.55f,
                    Operation: "ArtistQuestionSuggestions"),
                cancellationToken).ConfigureAwait(false);

            if (response.IsComplete
                && TryParseSuggestedQuestions(response.RawResponseText ?? response.Text, out var questions))
            {
                return questions;
            }

            _logger?.LogDebug("Artist question suggestions returned {Status}: {Error}",
                response.Status, response.ErrorMessage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "Artist question suggestion generation failed.");
        }

        return [];
    }

    private async Task<ArtistQuestionPlan> BuildPlanAsync(
        ArtistAiQuestionRequest request,
        IAiActivitySink activity,
        CancellationToken cancellationToken)
    {
        var deterministic = ArtistQuestionPlan.FromQuestion(request.Question);
        if (deterministic.Intent != ArtistQuestionIntent.General)
        {
            activity.Report(new AiActivityEvent(
                AiActivityKind.Planning,
                "Selected artist catalog tools",
                Detail: deterministic.Intent.ToString()));
            return deterministic;
        }

        activity.Report(new AiActivityEvent(
            AiActivityKind.Planning,
            "Planning tools for this question"));

        try
        {
            var response = await _model.GenerateStructuredJsonAsync(
                new AiStructuredGenerationRequest(
                    BuildPlannerPrompt(request),
                    PlannerJsonSchema,
                    Temperature: 0.0f,
                    Operation: "ArtistQuestionToolPlan"),
                cancellationToken).ConfigureAwait(false);

            if (response.IsComplete
                && TryParsePlan(response.RawResponseText ?? response.Text, out var planned))
            {
                activity.Report(new AiActivityEvent(
                    AiActivityKind.Planning,
                    "Planned artist tools",
                    Detail: planned.Intent.ToString()));
                return planned;
            }

            activity.Report(new AiActivityEvent(
                AiActivityKind.Warning,
                "Tool planner fell back to default artist context",
                Detail: response.ErrorMessage ?? response.Status.ToString()));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "Artist question tool planning failed.");
            activity.Report(new AiActivityEvent(
                AiActivityKind.Warning,
                "Tool planner fell back to default artist context",
                Detail: ex.Message));
        }

        return deterministic;
    }

    private async Task<ArtistQuestionContext> LoadContextAsync(
        ArtistAiQuestionRequest request,
        ArtistQuestionPlan plan,
        IAiActivitySink activity,
        CancellationToken cancellationToken)
    {
        ArtistProfileFacts? profile = null;
        IReadOnlyList<ArtistTrackFact> topTracks = [];
        IReadOnlyList<ArtistReleaseFact> releases = [];
        IReadOnlyList<ArtistTrackFact> releaseTracks = [];
        IReadOnlyList<WebSearchResult> webResults = [];
        WikipediaSummary? wikipedia = null;

        if (plan.UseProfile)
        {
            activity.Report(new AiActivityEvent(
                AiActivityKind.ToolStarted,
                "Reading artist profile",
                ToolName: "artist.profile"));
            profile = await _artistTools.GetProfileAsync(request.ArtistUri, cancellationToken).ConfigureAwait(false);
            activity.Report(new AiActivityEvent(
                AiActivityKind.ToolCompleted,
                "Read artist profile",
                ToolName: "artist.profile",
                Detail: profile.MonthlyListeners > 0 ? $"{profile.MonthlyListeners:N0} monthly listeners" : null));

            if (_wikipedia is not null)
            {
                activity.Report(new AiActivityEvent(
                    AiActivityKind.ToolStarted,
                    "Looking up Wikipedia",
                    ToolName: "wikipedia.lookup"));
                try
                {
                    wikipedia = await _wikipedia.LookupArtistAsync(request.ArtistName, cancellationToken).ConfigureAwait(false);
                    activity.Report(new AiActivityEvent(
                        AiActivityKind.ToolCompleted,
                        wikipedia is null ? "No Wikipedia article found" : $"Loaded Wikipedia summary for {wikipedia.Title}",
                        ToolName: "wikipedia.lookup"));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.LogDebug(ex, "Wikipedia lookup failed for {Artist}", request.ArtistName);
                    activity.Report(new AiActivityEvent(
                        AiActivityKind.Warning,
                        "Wikipedia lookup failed",
                        ToolName: "wikipedia.lookup",
                        Detail: ex.Message));
                }
            }
        }

        if (plan.UseTopTracks)
        {
            activity.Report(new AiActivityEvent(
                AiActivityKind.ToolStarted,
                "Loading popular tracks",
                ToolName: "artist.top_tracks"));
            topTracks = await _artistTools.GetTopTracksAsync(request.ArtistUri, cancellationToken).ConfigureAwait(false);
            activity.Report(new AiActivityEvent(
                AiActivityKind.ToolCompleted,
                $"Loaded {topTracks.Count} popular tracks",
                ToolName: "artist.top_tracks"));
        }

        if (plan.UseDiscography || plan.UseReleaseTracks)
        {
            activity.Report(new AiActivityEvent(
                AiActivityKind.ToolStarted,
                "Loading artist discography",
                ToolName: "artist.discography"));
            releases = await _artistTools.GetDiscographyAsync(request.ArtistUri, cancellationToken).ConfigureAwait(false);
            activity.Report(new AiActivityEvent(
                AiActivityKind.ToolCompleted,
                $"Loaded {releases.Count} releases",
                ToolName: "artist.discography"));
        }

        if (plan.UseReleaseTracks && releases.Count > 0)
        {
            var selectedReleases = SelectReleasesForTrackLookup(plan, releases);
            if (selectedReleases.Count > 0)
            {
                activity.Report(new AiActivityEvent(
                    AiActivityKind.ToolStarted,
                    $"Loading tracks from {selectedReleases.Count} releases",
                    ToolName: "artist.release_tracks"));
                releaseTracks = await _artistTools.GetReleaseTracksAsync(
                    request.ArtistUri,
                    selectedReleases,
                    selectedReleases.Count,
                    cancellationToken).ConfigureAwait(false);
                activity.Report(new AiActivityEvent(
                    AiActivityKind.ToolCompleted,
                    $"Loaded {releaseTracks.Count} release tracks",
                    ToolName: "artist.release_tracks"));
            }
        }

        if (plan.UseWebSearch)
        {
            if (_webSearch?.IsAvailable != true)
            {
                activity.Report(new AiActivityEvent(
                    AiActivityKind.ToolSkipped,
                    "Web search provider is not available",
                    ToolName: "web.search"));
            }
            else
            {
                activity.Report(new AiActivityEvent(
                    AiActivityKind.ToolStarted,
                    "Searching the web",
                    ToolName: "web.search"));
                try
                {
                    webResults = await _webSearch.SearchAsync(
                        $"{request.ArtistName} {request.Question}",
                        new WebSearchOptions(MaxResults: 5),
                        cancellationToken).ConfigureAwait(false);
                    activity.Report(new AiActivityEvent(
                        AiActivityKind.ToolCompleted,
                        $"Found {webResults.Count} web result{(webResults.Count == 1 ? "" : "s")}",
                        ToolName: "web.search"));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.LogDebug(ex, "Web search failed for {Artist}/{Question}", request.ArtistName, request.Question);
                    activity.Report(new AiActivityEvent(
                        AiActivityKind.Warning,
                        "Web search failed",
                        ToolName: "web.search",
                        Detail: ex.Message));
                }
            }
        }

        return new ArtistQuestionContext(profile, topTracks, releases, releaseTracks, webResults, wikipedia);
    }

    private static string BuildAnswerPrompt(
        ArtistAiQuestionRequest request,
        ArtistQuestionPlan plan,
        ArtistQuestionContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are Wavee's artist assistant inside a Spotify desktop client.");
        sb.AppendLine("Answer the user's artist question directly and concisely.");
        sb.AppendLine("Treat evidence hierarchically: SPOTIFY_BIOGRAPHY and the artist catalog signals (POPULAR_TRACKS, RELEASES, TRACKS_FETCHED_FROM_RELEASES, PROFILE_SIGNALS) are primary evidence. WIKIPEDIA is reliable secondary context. WEB_RESULTS are supporting background — use them only when consistent with the above.");
        sb.AppendLine("You may use trained music-domain knowledge for broad, well-known context, but do not invent precise facts not supported by the supplied signals.");
        sb.AppendLine("If the question asks for 'best', distinguish popularity, historical importance, and personal taste when helpful.");
        sb.AppendLine("If the question asks for lesser-known songs, avoid only naming the most popular tracks unless you explain why catalog data is limited.");
        sb.AppendLine("If RECOMMENDED_ITEMS is present, anchor the answer to those exact items and do not introduce unrelated song or release titles.");
        sb.AppendLine("For song-list questions, cover 3-5 recommendations unless the user clearly asks for one single pick.");
        sb.AppendLine("Do not recommend modified versions such as slowed, sped-up, instrumental, karaoke, remix, edit, live, demo, or acapella tracks unless the user explicitly asks for those versions.");
        sb.AppendLine("Do not mention internal tool names. Do not use markdown tables. Short bullets are allowed for song lists.");
        sb.AppendLine();
        sb.Append("ARTIST: ").AppendLine(request.ArtistName);
        sb.Append("QUESTION: ").AppendLine(request.Question.Trim());
        sb.Append("PLANNED_INTENT: ").AppendLine(plan.Intent.ToString());
        sb.Append("TODAY: ").AppendLine(DateTimeOffset.Now.ToString("yyyy-MM-dd"));
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(request.Biography))
        {
            sb.AppendLine("SPOTIFY_BIOGRAPHY:");
            sb.AppendLine(TrimForPrompt(request.Biography!, 1400));
            sb.AppendLine();
        }

        if (context.Wikipedia is { } wiki && !string.IsNullOrWhiteSpace(wiki.Extract))
        {
            sb.AppendLine("WIKIPEDIA:");
            if (!string.IsNullOrWhiteSpace(wiki.Description))
                sb.Append("Summary: ").AppendLine(wiki.Description);
            sb.AppendLine(TrimForPrompt(wiki.Extract, 1200));
            sb.AppendLine();
        }

        if (request.KnownTopTrackNames is { Count: > 0 })
        {
            sb.Append("VISIBLE_TOP_TRACKS: ");
            sb.AppendLine(string.Join(", ", request.KnownTopTrackNames.Take(8)));
            sb.AppendLine();
        }

        if (context.Profile is { } profile)
        {
            sb.AppendLine("PROFILE_SIGNALS:");
            if (profile.MonthlyListeners > 0)
                sb.Append("Monthly listeners: ").Append(profile.MonthlyListeners.ToString("N0")).AppendLine();
            if (profile.Followers > 0)
                sb.Append("Followers: ").Append(profile.Followers.ToString("N0")).AppendLine();
            if (profile.WorldRank is { } rank)
                sb.Append("World rank: ").Append(rank).AppendLine();
            if (profile.TopCities.Count > 0)
                sb.Append("Top cities: ").AppendLine(string.Join(", ", profile.TopCities.Take(8)));
            if (!string.IsNullOrWhiteSpace(profile.Biography))
                sb.Append("Profile bio excerpt: ").AppendLine(TrimForPrompt(profile.Biography!, 800));
            sb.AppendLine();
        }

        if (context.TopTracks.Count > 0)
        {
            sb.AppendLine("POPULAR_TRACKS:");
            foreach (var track in context.TopTracks
                         .Where(t => !string.IsNullOrWhiteSpace(t.Title))
                         .OrderByDescending(t => t.PlayCount)
                         .Take(20))
            {
                sb.Append("- ").Append(track.Title);
                if (!string.IsNullOrWhiteSpace(track.AlbumName))
                    sb.Append(" (").Append(track.AlbumName).Append(')');
                if (track.Year is { } year and > 0)
                    sb.Append(", ").Append(year);
                if (track.PlayCount > 0)
                    sb.Append(", plays ").Append(track.PlayCount.ToString("N0"));
                sb.AppendLine();
            }
            sb.AppendLine();
        }

        if (context.Releases.Count > 0)
        {
            sb.AppendLine("RELEASES:");
            foreach (var release in context.Releases
                         .Where(r => !string.IsNullOrWhiteSpace(r.Name))
                         .OrderBy(r => r.ReleaseDate)
                         .Take(40))
            {
                sb.Append("- ").Append(release.Name)
                    .Append(" [").Append(release.Type).Append(']');
                if (release.Year > 0)
                    sb.Append(", ").Append(release.Year);
                if (release.TrackCount > 0)
                    sb.Append(", ").Append(release.TrackCount).Append(" tracks");
                sb.AppendLine();
            }
            sb.AppendLine();
        }

        if (context.ReleaseTracks.Count > 0)
        {
            sb.AppendLine("TRACKS_FETCHED_FROM_RELEASES:");
            foreach (var track in CanonicalTracks(context.ReleaseTracks)
                         .OrderBy(t => t.ReleaseDate == default ? DateTimeOffset.MaxValue : t.ReleaseDate)
                         .ThenBy(t => t.TrackNumber <= 0 ? int.MaxValue : t.TrackNumber)
                         .Take(40))
            {
                sb.Append("- ").Append(track.Title);
                if (!string.IsNullOrWhiteSpace(track.AlbumName))
                    sb.Append(" (").Append(track.AlbumName).Append(')');
                if (track.Year is { } year and > 0)
                    sb.Append(", ").Append(year);
                if (track.PlayCount > 0)
                    sb.Append(", plays ").Append(track.PlayCount.ToString("N0"));
                sb.AppendLine();
            }
            sb.AppendLine();
        }

        if (context.WebResults.Count > 0)
        {
            sb.AppendLine("WEB_RESULTS:");
            foreach (var result in context.WebResults.Take(5))
            {
                sb.Append("- ").Append(result.Title).Append(" — ").Append(result.Snippet);
                if (!string.IsNullOrWhiteSpace(result.Source))
                    sb.Append(" (").Append(result.Source).Append(')');
                sb.AppendLine();
            }
            sb.AppendLine();
        }

        var recommendations = BuildRecommendations(plan, context);
        if (recommendations.Count > 0)
        {
            sb.AppendLine("RECOMMENDED_ITEMS:");
            foreach (var item in recommendations)
            {
                sb.Append("- ").Append(item.Kind).Append(": ").Append(item.Title);
                if (!string.IsNullOrWhiteSpace(item.Subtitle))
                    sb.Append(" — ").Append(item.Subtitle);
                if (!string.IsNullOrWhiteSpace(item.Reason))
                    sb.Append(" [").Append(item.Reason).Append(']');
                sb.AppendLine();
            }
            sb.AppendLine();
        }

        sb.AppendLine("Now answer the question.");
        return TrimForPrompt(sb.ToString(), MaxPromptCharacters);
    }

    private static IReadOnlyList<ArtistAiRecommendation> BuildRecommendations(
        ArtistQuestionPlan plan,
        ArtistQuestionContext context)
    {
        return plan.Intent switch
        {
            ArtistQuestionIntent.OldestSongs => BuildOldestReleaseRecommendations(context),
            ArtistQuestionIntent.LesserKnownSongs => BuildLesserKnownTrackRecommendations(context),
            ArtistQuestionIntent.BestKnownSongs => BuildBestKnownTrackRecommendations(context),
            _ => [],
        };
    }

    private static IReadOnlyList<ArtistAiRecommendation> BuildBestKnownTrackRecommendations(
        ArtistQuestionContext context)
        => CanonicalTracks(context.TopTracks)
            .OrderByDescending(t => t.PlayCount)
            .Take(5)
            .Select(t => new ArtistAiRecommendation(
                ArtistAiRecommendationKind.Track,
                t.Title!,
                BuildTrackSubtitle(t),
                t.Uri,
                t.ImageUrl,
                ContextUri: t.AlbumUri,
                Reason: t.PlayCount > 0 ? $"{t.PlayCount:N0} plays in Spotify top-track data" : "Popular track signal",
                PlayCount: t.PlayCount,
                Year: t.Year))
            .ToList();

    private static IReadOnlyList<ArtistAiRecommendation> BuildLesserKnownTrackRecommendations(
        ArtistQuestionContext context)
    {
        var topTrackNames = CanonicalTracks(context.TopTracks)
            .OrderByDescending(t => t.PlayCount)
            .Take(10)
            .Select(t => NormalizeTrackKey(t.Title))
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .ToHashSet(StringComparer.Ordinal);

        var candidates = CanonicalTracks(context.ReleaseTracks)
            .Where(t => !topTrackNames.Contains(NormalizeTrackKey(t.Title)))
            .OrderBy(t => t.PlayCount <= 0 ? long.MaxValue : t.PlayCount)
            .ThenBy(t => t.Year ?? int.MaxValue)
            .ThenBy(t => t.Title)
            .DistinctBy(t => NormalizeTrackKey(t.Title))
            .Take(5)
            .ToList();

        if (candidates.Count < 5)
        {
            candidates.AddRange(CanonicalTracks(context.TopTracks)
                .Where(t => !topTrackNames.Contains(NormalizeTrackKey(t.Title)) || candidates.Count == 0)
                .OrderBy(t => t.PlayCount <= 0 ? long.MaxValue : t.PlayCount)
                .ThenBy(t => t.Title)
                .DistinctBy(t => NormalizeTrackKey(t.Title))
                .Where(t => candidates.All(existing =>
                    !string.Equals(NormalizeTrackKey(existing.Title), NormalizeTrackKey(t.Title), StringComparison.Ordinal)))
                .Take(5 - candidates.Count));
        }

        return candidates
            .OrderBy(t => t.PlayCount <= 0 ? long.MaxValue : t.PlayCount)
            .ThenBy(t => t.Year ?? int.MaxValue)
            .Select(t => new ArtistAiRecommendation(
                ArtistAiRecommendationKind.Track,
                t.Title!,
                BuildTrackSubtitle(t),
                t.Uri,
                t.ImageUrl,
                ContextUri: t.AlbumUri,
                Reason: t.PlayCount > 0 ? "Lower play count among loaded catalog tracks" : "Catalog track from release tracklist",
                PlayCount: t.PlayCount,
                Year: t.Year))
            .ToList();
    }

    private static IReadOnlyList<ArtistAiRecommendation> BuildOldestReleaseRecommendations(
        ArtistQuestionContext context)
    {
        var tracks = CanonicalTracks(context.ReleaseTracks)
            .OrderBy(t => t.ReleaseDate == default ? DateTimeOffset.MaxValue : t.ReleaseDate)
            .ThenBy(t => t.TrackNumber <= 0 ? int.MaxValue : t.TrackNumber)
            .ThenBy(t => t.Title)
            .DistinctBy(t => NormalizeTrackKey(t.Title))
            .Take(5)
            .Select(t => new ArtistAiRecommendation(
                ArtistAiRecommendationKind.Track,
                t.Title!,
                BuildTrackSubtitle(t),
                t.Uri,
                t.ImageUrl,
                ContextUri: t.AlbumUri,
                Reason: "Earliest loaded release track",
                PlayCount: t.PlayCount,
                Year: t.Year))
            .ToList();

        if (tracks.Count > 0)
            return tracks;

        return context.Releases
            .Where(r => !string.IsNullOrWhiteSpace(r.Name))
            .Where(r => !IsModifiedTitle(r.Name))
            .OrderBy(r => r.ReleaseDate == default ? DateTimeOffset.MaxValue : r.ReleaseDate)
            .ThenBy(r => r.Name)
            .Take(5)
            .Select(r => new ArtistAiRecommendation(
                ArtistAiRecommendationKind.Release,
                r.Name!,
                BuildReleaseSubtitle(r),
                r.Uri,
                r.ImageUrl,
                Reason: "Earliest loaded release",
                Year: r.Year))
            .ToList();
    }

    private static IReadOnlyList<ArtistReleaseFact> SelectReleasesForTrackLookup(
        ArtistQuestionPlan plan,
        IReadOnlyList<ArtistReleaseFact> releases)
    {
        var ordered = releases
            .Where(r => !string.IsNullOrWhiteSpace(r.Uri))
            .Where(r => r.TrackCount != 0)
            .Where(r => !IsModifiedTitle(r.Name))
            .OrderBy(r => r.ReleaseDate == default ? DateTimeOffset.MaxValue : r.ReleaseDate)
            .ThenBy(r => r.Name)
            .ToList();

        return plan.Intent switch
        {
            ArtistQuestionIntent.OldestSongs => ordered.Take(24).ToList(),
            ArtistQuestionIntent.LesserKnownSongs => ordered.Take(24).ToList(),
            _ => ordered.Take(12).ToList(),
        };
    }

    private static IEnumerable<ArtistTrackFact> CanonicalTracks(IEnumerable<ArtistTrackFact> tracks)
        => tracks.Where(t => !string.IsNullOrWhiteSpace(t.Title))
            .Where(t => !IsModifiedTitle(t.Title))
            .Where(t => !IsModifiedTitle(t.AlbumName));

    private static string NormalizeTrackKey(string? title)
        => string.IsNullOrWhiteSpace(title)
            ? string.Empty
            : title.Trim().ToLowerInvariant();

    private static bool IsModifiedTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return false;

        var normalized = title.Trim().ToLowerInvariant();
        return ContainsAny(normalized,
            "slowed",
            "slowd",
            "sped up",
            "sped-up",
            "speed up",
            "instrumental",
            "karaoke",
            "acapella",
            "a cappella",
            "nightcore",
            "8d audio",
            "lo-fi",
            "lofi",
            "remix",
            "radio edit",
            "extended mix",
            "club mix",
            "vip mix",
            "demo",
            "live at",
            "live from",
            "(live",
            "[live",
            " - live");
    }

    private static bool ContainsAny(string haystack, params string[] needles)
        => needles.Any(n => haystack.Contains(n, StringComparison.Ordinal));

    private static string? BuildTrackSubtitle(ArtistTrackFact track)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(track.AlbumName))
            parts.Add(track.AlbumName!);
        if (track.Year is { } year and > 0)
            parts.Add(year.ToString(System.Globalization.CultureInfo.CurrentCulture));
        if (track.PlayCount > 0)
            parts.Add($"{track.PlayCount:N0} plays");
        return parts.Count == 0 ? null : string.Join(" • ", parts);
    }

    private static string? BuildReleaseSubtitle(ArtistReleaseFact release)
    {
        var parts = new List<string>(3);
        if (release.Year > 0)
            parts.Add(release.Year.ToString(System.Globalization.CultureInfo.CurrentCulture));
        if (!string.IsNullOrWhiteSpace(release.Type))
            parts.Add(release.Type.ToLowerInvariant());
        if (release.TrackCount > 0)
            parts.Add($"{release.TrackCount} track{(release.TrackCount == 1 ? "" : "s")}");
        return parts.Count == 0 ? null : string.Join(" • ", parts);
    }

    private static string BuildFallbackAnswer(
        ArtistQuestionPlan plan,
        IReadOnlyList<ArtistAiRecommendation> recommendations)
    {
        var intro = plan.Intent switch
        {
            ArtistQuestionIntent.OldestSongs => "Based on the loaded discography, these are the earliest releases I found:",
            ArtistQuestionIntent.LesserKnownSongs => "Based on the loaded track data, these are less obvious picks to try:",
            ArtistQuestionIntent.BestKnownSongs => "Based on Spotify's loaded top-track data, these are the strongest candidates:",
            _ => "Here are the best grounded matches I found:",
        };

        return intro + " " + string.Join(", ", recommendations.Select(r => r.Title));
    }

    private static string BuildPlannerPrompt(ArtistAiQuestionRequest request)
        => "Choose tools for an artist-question assistant.\n" +
           "Available tools: artist.profile, artist.top_tracks, artist.discography, artist.release_tracks.\n" +
           "Use artist.discography for oldest, early, albums, releases, lesser-known, deep cuts, or catalog questions.\n" +
           "Use artist.release_tracks for oldest songs, lesser-known songs, deep cuts, or questions that need tracks inside releases.\n" +
           "Use artist.top_tracks for best, biggest, most popular, hit, or recommendation questions.\n\n" +
           $"Artist: {request.ArtistName}\nQuestion: {request.Question}\n";

    private static string BuildSuggestionPrompt(ArtistAiSuggestionRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Generate short suggested action chips for an artist assistant inside a Spotify desktop client.");
        sb.AppendLine("Return questions a listener would tap to ask about this artist's catalog.");
        sb.AppendLine("Return 4 suggestions. Each should be concise, natural, and under 70 characters.");
        sb.AppendLine("Prefer artist-specific wording. Use the artist name or a clearly fitting pronoun such as his/her when the biography makes that safe.");
        sb.AppendLine("Never use the word 'their'.");
        sb.AppendLine("Cover a mix of: oldest songs, best song, lesser-known songs, releases, or where to start.");
        sb.AppendLine();
        sb.Append("ARTIST: ").AppendLine(request.ArtistName);

        if (!string.IsNullOrWhiteSpace(request.Biography))
        {
            sb.AppendLine("BIOGRAPHY:");
            sb.AppendLine(TrimForPrompt(request.Biography!, 900));
        }

        if (request.KnownTopTrackNames is { Count: > 0 })
        {
            sb.Append("VISIBLE_TOP_TRACKS: ");
            sb.AppendLine(string.Join(", ", request.KnownTopTrackNames.Take(8)));
        }

        return TrimForPrompt(sb.ToString(), 2400);
    }

    private static bool TryParsePlan(string? json, out ArtistQuestionPlan plan)
    {
        plan = ArtistQuestionPlan.Default;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var intent = root.TryGetProperty("intent", out var intentEl)
                ? ParseIntent(intentEl.GetString())
                : ArtistQuestionIntent.General;

            var useProfile = true;
            var useTopTracks = false;
            var useDiscography = false;
            var useReleaseTracks = false;
            var useWebSearch = false;
            if (root.TryGetProperty("tools", out var toolsEl)
                && toolsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var toolEl in toolsEl.EnumerateArray())
                {
                    var tool = toolEl.GetString();
                    useTopTracks |= string.Equals(tool, "artist.top_tracks", StringComparison.OrdinalIgnoreCase);
                    useDiscography |= string.Equals(tool, "artist.discography", StringComparison.OrdinalIgnoreCase);
                    useReleaseTracks |= string.Equals(tool, "artist.release_tracks", StringComparison.OrdinalIgnoreCase);
                }
            }

            useReleaseTracks |= intent is ArtistQuestionIntent.OldestSongs or ArtistQuestionIntent.LesserKnownSongs;
            useDiscography |= useReleaseTracks;
            useWebSearch = true; // Always ground artist answers with at least one web search.

            plan = new ArtistQuestionPlan(intent, useProfile, useTopTracks, useDiscography, useReleaseTracks, useWebSearch);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseSuggestedQuestions(string? json, out IReadOnlyList<string> questions)
    {
        questions = [];
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("questions", out var questionsEl)
                || questionsEl.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var values = new List<string>(4);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in questionsEl.EnumerateArray())
            {
                var question = CleanSuggestedQuestion(item.GetString());
                if (question is null || !seen.Add(question))
                    continue;

                values.Add(question);
                if (values.Count >= 4)
                    break;
            }

            questions = values;
            return values.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? CleanSuggestedQuestion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var question = value.Trim().Trim('"', '\'', '“', '”');
        if (question.Length is < 8 or > 90)
            return null;
        if (question.Contains("their", StringComparison.OrdinalIgnoreCase))
            return null;

        return question;
    }

    private static ArtistQuestionIntent ParseIntent(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "oldest_songs" => ArtistQuestionIntent.OldestSongs,
            "best_known_songs" => ArtistQuestionIntent.BestKnownSongs,
            "lesser_known_songs" => ArtistQuestionIntent.LesserKnownSongs,
            "recent_or_current" => ArtistQuestionIntent.RecentOrCurrent,
            _ => ArtistQuestionIntent.General,
        };

    private static float TemperatureFor(ArtistQuestionPlan plan)
        => plan.Intent == ArtistQuestionIntent.General ? 0.35f : 0.25f;

    private static string CleanAnswer(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    private static string TrimForPrompt(string value, int maxCharacters)
    {
        if (value.Length <= maxCharacters)
            return value;

        var trimmed = value[..maxCharacters];
        var lastSpace = trimmed.LastIndexOf(' ');
        if (lastSpace > maxCharacters / 2)
            trimmed = trimmed[..lastSpace];
        return trimmed.TrimEnd() + "...";
    }

    private const string PlannerJsonSchema = """
    {
      "type": "object",
      "properties": {
        "intent": {
          "type": "string",
          "enum": [
            "general",
            "oldest_songs",
            "best_known_songs",
            "lesser_known_songs",
            "recent_or_current"
          ]
        },
        "tools": {
          "type": "array",
          "items": {
            "type": "string",
            "enum": [
              "artist.profile",
              "artist.top_tracks",
              "artist.discography",
              "artist.release_tracks"
            ]
          }
        }
      },
      "required": [ "intent", "tools" ]
    }
    """;

    private const string SuggestionJsonSchema = """
    {
      "type": "object",
      "properties": {
        "questions": {
          "type": "array",
          "minItems": 4,
          "maxItems": 4,
          "items": {
            "type": "string"
          }
        }
      },
      "required": [ "questions" ]
    }
    """;

    private sealed record ArtistQuestionContext(
        ArtistProfileFacts? Profile,
        IReadOnlyList<ArtistTrackFact> TopTracks,
        IReadOnlyList<ArtistReleaseFact> Releases,
        IReadOnlyList<ArtistTrackFact> ReleaseTracks,
        IReadOnlyList<WebSearchResult> WebResults,
        WikipediaSummary? Wikipedia);

    private sealed record ArtistQuestionPlan(
        ArtistQuestionIntent Intent,
        bool UseProfile,
        bool UseTopTracks,
        bool UseDiscography,
        bool UseReleaseTracks,
        bool UseWebSearch)
    {
        public static ArtistQuestionPlan Default { get; } =
            new(ArtistQuestionIntent.General, UseProfile: true, UseTopTracks: true, UseDiscography: false, UseReleaseTracks: false, UseWebSearch: true);

        public static ArtistQuestionPlan FromQuestion(string question)
        {
            var q = question.Trim().ToLowerInvariant();
            if (ContainsAny(q, "oldest", "earliest", "first song", "first songs", "early songs", "debut"))
                return new(ArtistQuestionIntent.OldestSongs, true, true, true, true, true);
            if (ContainsAny(q, "lesser known", "lesser-known", "deep cut", "deep cuts", "underrated", "hidden gem", "hidden gems", "obscure"))
                return new(ArtistQuestionIntent.LesserKnownSongs, true, true, true, true, true);
            if (ContainsAny(q, "best song", "best songs", "best track", "best tracks", "biggest", "most popular", "hit", "hits", "classic"))
                return new(ArtistQuestionIntent.BestKnownSongs, true, true, true, false, true);
            if (ContainsAny(q, "latest", "recent", "newest", "news", "tour", "concert", "2026", "2025"))
                return new(ArtistQuestionIntent.RecentOrCurrent, true, true, false, false, true);

            return Default;
        }

        private static bool ContainsAny(string haystack, params string[] needles)
            => needles.Any(n => haystack.Contains(n, StringComparison.Ordinal));
    }

    private enum ArtistQuestionIntent
    {
        General,
        OldestSongs,
        BestKnownSongs,
        LesserKnownSongs,
        RecentOrCurrent,
    }
}
