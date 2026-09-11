using Wavee.Backend.Catalog;
using Wavee.Core;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// The batch-vs-envelope handoff. The bug this locks: a USER_PROFILE payload carrying only a username decoded to a
/// <c>Present</c> facet with no name and no avatar, which suppressed the profile endpoint — so a playlist's
/// collaborators rendered their bare Spotify ids forever (the facet was Present, so nothing refetched it).
/// </summary>
public sealed class BatchAnswerPolicyTests
{
    static ResourceFetchResult Profile(string? name, string? avatarUrl) => ResourceFetchResult.Present(
        new ReplaceFacetPatch(new UserIdentityValue(name, avatarUrl is null ? null : new Image(avatarUrl))));

    [Fact]
    public void APresentProfileWithNeitherNameNorAvatarStillNeedsTheProfileEndpoint()
    {
        Assert.True(BatchAnswerPolicy.NeedsEnvelopeFallback(FacetKind.UserIdentity, Profile(null, null)));
        Assert.True(BatchAnswerPolicy.NeedsEnvelopeFallback(FacetKind.UserIdentity, Profile("", null)));
        Assert.True(BatchAnswerPolicy.NeedsEnvelopeFallback(FacetKind.UserIdentity, Profile("   ", null)));
    }

    [Fact]
    public void APresentProfileWithANameOrAnAvatarSettlesTheKey()
    {
        Assert.False(BatchAnswerPolicy.NeedsEnvelopeFallback(FacetKind.UserIdentity, Profile("Christos", null)));
        Assert.False(BatchAnswerPolicy.NeedsEnvelopeFallback(FacetKind.UserIdentity, Profile(null, "https://i/a")));
        Assert.False(BatchAnswerPolicy.NeedsEnvelopeFallback(FacetKind.UserIdentity, Profile("Christos", "https://i/a")));
    }

    [Fact]
    public void AnAbsentOrUndecodableProfileFallsThroughToTheProfileEndpoint()
    {
        Assert.True(BatchAnswerPolicy.NeedsEnvelopeFallback(FacetKind.UserIdentity, ResourceFetchResult.Absent()));
        Assert.True(BatchAnswerPolicy.NeedsEnvelopeFallback(FacetKind.UserIdentity,
            ResourceFetchResult.Failed(new(ResourceErrorKind.Decode, "undecodable USER_PROFILE payload"))));
        Assert.True(BatchAnswerPolicy.NeedsEnvelopeFallback(FacetKind.UserIdentity,
            ResourceFetchResult.Failed(new(ResourceErrorKind.InvalidResponse, "extension omitted"))));
    }

    /// <summary>A transport failure, a 429 or a 403 must NOT become a second per-key request: the envelope arm hits
    /// the same wall, and that is how a rate limit turns into an outage.</summary>
    [Theory]
    [InlineData(ResourceErrorKind.Transport)]
    [InlineData(ResourceErrorKind.RateLimited)]
    [InlineData(ResourceErrorKind.Forbidden)]
    public void ATransportRateLimitOrForbiddenFailureIsNeverRetriedByTheEnvelopeArm(ResourceErrorKind kind)
    {
        Assert.False(BatchAnswerPolicy.NeedsEnvelopeFallback(FacetKind.UserIdentity,
            ResourceFetchResult.Failed(new(kind, "no"))));
        Assert.False(BatchAnswerPolicy.NeedsEnvelopeFallback(FacetKind.Availability,
            ResourceFetchResult.Failed(new(kind, "no"))));
    }

    /// <summary>Unsupported is a verdict about the KEY (a document kind asked of the wrong entity kind), so the
    /// envelope arm cannot answer it either.</summary>
    [Fact]
    public void UnsupportedIsNotRetried()
    {
        Assert.False(BatchAnswerPolicy.NeedsEnvelopeFallback(FacetKind.UserIdentity,
            new ResourceFetchResult(ResourceFetchStatus.Unsupported)));
    }

    /// <summary>Availability keeps its landed behaviour exactly: Present settles it, Absent falls through. Only the
    /// UserIdentity arm gained the renderability test.</summary>
    [Fact]
    public void AvailabilityKeepsItsLandedBehaviour()
    {
        Assert.False(BatchAnswerPolicy.NeedsEnvelopeFallback(FacetKind.Availability,
            ResourceFetchResult.Present(new ReplaceFacetPatch(new UserIdentityValue("ignored")))));
        Assert.True(BatchAnswerPolicy.NeedsEnvelopeFallback(FacetKind.Availability, ResourceFetchResult.Absent()));
    }

    /// <summary>No other facet has an envelope endpoint, so none may cost a second request — whatever the batch said.</summary>
    [Theory]
    [InlineData(FacetKind.TrackIdentity)]
    [InlineData(FacetKind.PlaylistHeader)]
    [InlineData(FacetKind.AlbumIdentity)]
    public void FacetsWithNoEnvelopeArmNeverFallBack(FacetKind facet)
    {
        Assert.False(BatchAnswerPolicy.NeedsEnvelopeFallback(facet, ResourceFetchResult.Absent()));
        Assert.False(BatchAnswerPolicy.NeedsEnvelopeFallback(facet,
            ResourceFetchResult.Failed(new(ResourceErrorKind.Decode, "no"))));
    }
}
