using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Wavee.AI;
using Wavee.AI.Artists;
using Wavee.AI.Generation;

namespace Wavee.UI.Tests.AI;

public sealed class ArtistAiQuestionServiceTests
{
    [Fact]
    public async Task AskAsync_FiltersSongQuestionsByVerifiedArtistCredits()
    {
        var model = new FakeLanguageModel();
        var tools = new FakeArtistTools
        {
            Releases =
            [
                new ArtistReleaseFact(
                    "don't shop when ur hungry !!",
                    "spotify:album:hungry",
                    "SINGLE",
                    ImageUrl: null,
                    DateTimeOffset.Parse("2023-09-15"),
                    TrackCount: 1,
                    Label: null,
                    Year: 2023)
            ],
            ReleaseTracks =
            [
                Track("don't shop when ur hungry !!", "spotify:track:hungry", ["salem ilese", "vaultboy"], album: "don't shop when ur hungry !!"),
                Track("Mad at Disney", "spotify:track:disney", ["salem ilese"], album: "Mad at Disney")
            ],
            TopTracks =
            [
                Track("Mad at Disney", "spotify:track:disney", ["salem ilese"], playCount: 500_000_000)
            ]
        };
        var search = new FakeCatalogSearch
        {
            Tracks =
            [
                Track("closer", "spotify:track:closer", ["vaultboy", "salem ilese"], album: "everything and nothing"),
                Track("everything sucks", "spotify:track:sucks", ["vaultboy"], album: "vaultboy")
            ]
        };
        var service = new ArtistAiQuestionService(
            model,
            tools,
            EnabledSettings.Instance,
            catalogSearch: search);

        var result = await service.AskAsync(new ArtistAiQuestionRequest(
            "spotify:artist:salem",
            "salem ilese",
            "what are some songs that feature vaultboy?"));

        result.Kind.Should().Be(ArtistAiQuestionResultKind.Ok);
        result.Text.Should().Contain("don't shop when ur hungry !!");
        result.Text.Should().Contain("closer");
        result.Text.Should().NotContain("Mad at Disney");
        result.Text.Should().NotContain("everything sucks");
        result.Recommendations.Should().NotBeNull();
        result.Recommendations!.Select(r => r.Title).Should().Equal("don't shop when ur hungry !!", "closer");
        model.GenerateTextCalls.Should().Be(0);
    }

    [Fact]
    public async Task AskAsync_AnswersReleaseFiltersFromCatalogWithoutModelFacts()
    {
        var model = new FakeLanguageModel();
        var tools = new FakeArtistTools
        {
            Releases =
            [
                new ArtistReleaseFact("Album 2020", "spotify:album:2020", "ALBUM", null, DateTimeOffset.Parse("2020-01-01"), 10, null, 2020),
                new ArtistReleaseFact("Single 2020", "spotify:album:single", "SINGLE", null, DateTimeOffset.Parse("2020-02-01"), 1, null, 2020),
                new ArtistReleaseFact("Album 2021", "spotify:album:2021", "ALBUM", null, DateTimeOffset.Parse("2021-01-01"), 10, null, 2021)
            ]
        };
        var service = new ArtistAiQuestionService(model, tools, EnabledSettings.Instance);

        var result = await service.AskAsync(new ArtistAiQuestionRequest(
            "spotify:artist:salem",
            "salem ilese",
            "what albums did she release in 2020?"));

        result.Kind.Should().Be(ArtistAiQuestionResultKind.Ok);
        result.Text.Should().Contain("Album 2020");
        result.Text.Should().NotContain("Single 2020");
        result.Text.Should().NotContain("Album 2021");
        result.Recommendations.Should().NotBeNull();
        result.Recommendations!.Select(r => r.Title).Should().Equal("Album 2020");
        model.GenerateTextCalls.Should().Be(0);
    }

    private static ArtistTrackFact Track(
        string title,
        string uri,
        IReadOnlyList<string> artists,
        string? album = null,
        long playCount = 0)
        => new(
            title,
            uri,
            album,
            AlbumUri: album is null ? null : $"spotify:album:{album}",
            ImageUrl: null,
            playCount,
            Year: null,
            ArtistNames: artists);

    private sealed class FakeArtistTools : IArtistAiToolProvider
    {
        public IReadOnlyList<ArtistReleaseFact> Releases { get; init; } = [];
        public IReadOnlyList<ArtistTrackFact> TopTracks { get; init; } = [];
        public IReadOnlyList<ArtistTrackFact> ReleaseTracks { get; init; } = [];

        public Task<ArtistProfileFacts> GetProfileAsync(string artistUri, CancellationToken cancellationToken = default)
            => Task.FromResult(new ArtistProfileFacts(artistUri, "salem ilese", null, 0, 0, null, []));

        public Task<IReadOnlyList<ArtistTrackFact>> GetTopTracksAsync(string artistUri, CancellationToken cancellationToken = default)
            => Task.FromResult(TopTracks);

        public Task<IReadOnlyList<ArtistReleaseFact>> GetDiscographyAsync(string artistUri, CancellationToken cancellationToken = default)
            => Task.FromResult(Releases);

        public Task<IReadOnlyList<ArtistTrackFact>> GetReleaseTracksAsync(
            string artistUri,
            IReadOnlyList<ArtistReleaseFact> releases,
            int maxReleases = 24,
            CancellationToken cancellationToken = default)
            => Task.FromResult(ReleaseTracks);
    }

    private sealed class FakeCatalogSearch : IMusicCatalogSearchProvider
    {
        public IReadOnlyList<ArtistTrackFact> Tracks { get; init; } = [];
        public bool IsAvailable => true;

        public Task<IReadOnlyList<ArtistSearchFact>> SearchArtistsAsync(string query, int limit = 5, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ArtistSearchFact>>([]);

        public Task<IReadOnlyList<ArtistTrackFact>> SearchTracksAsync(string query, int limit = 20, CancellationToken cancellationToken = default)
            => Task.FromResult(Tracks);
    }

    private sealed class FakeLanguageModel : ILanguageModelClient
    {
        public int GenerateTextCalls { get; private set; }
        public bool IsSupported => true;
        public string DescribeStatus() => "ready";

        public Task<bool> EnsureReadyAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<AiGenerationResult> GenerateTextAsync(
            AiTextGenerationRequest request,
            IProgress<string>? deltaProgress = null,
            CancellationToken cancellationToken = default)
        {
            GenerateTextCalls++;
            return Task.FromResult(new AiGenerationResult(AiGenerationStatus.Complete, "model answer"));
        }

        public Task<AiGenerationResult> GenerateStructuredJsonAsync(
            AiStructuredGenerationRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new AiGenerationResult(AiGenerationStatus.ResponseInvalidJson, string.Empty));
    }

    private sealed class EnabledSettings : IAiFeatureSettings
    {
        public static EnabledSettings Instance { get; } = new();
        public bool AiFeaturesEnabled => true;
        public bool AiLyricsSummarizeEnabled => true;
        public bool AiBioSummarizeEnabled => true;
        public bool AiAlbumSummarizeEnabled => true;
    }
}
