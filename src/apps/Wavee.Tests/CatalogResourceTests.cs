using System;
using System.Collections.Generic;
using Wavee.Backend.Catalog;
using Wavee.Core;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

public sealed class CatalogResourceTests
{
    [Fact]
    public void NativeOriginSeed_RemainsLocalUntilOwningProviderExplicitlyChangesIt()
    {
        CatalogValue value = new TrackIdentityPatch(Origin: FieldChange<TrackOrigin>.Set(TrackOrigin.Local)).Apply(null, true);
        value = new TrackIdentityPatch(Title: FieldChange<string?>.Set("Local title")).Apply(value, true);
        Assert.Equal(TrackOrigin.Local, Assert.IsType<TrackIdentityValue>(value).Origin);
        value = new TrackIdentityPatch(Origin: FieldChange<TrackOrigin>.Set(TrackOrigin.Streamed)).Apply(value, true);
        Assert.Equal(TrackOrigin.Local, Assert.IsType<TrackIdentityValue>(value).Origin);
        value = new TrackIdentityPatch(Origin: FieldChange<TrackOrigin>.Set(TrackOrigin.Streamed)).Apply(value);
        Assert.Equal(TrackOrigin.Streamed, Assert.IsType<TrackIdentityValue>(value).Origin);
    }

    [Fact]
    public void PlaylistExpiry_UsesFutureRolloverAndBacksOffAnAlreadyDueHeader()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(TimeSpan.FromSeconds(5), ResourcePolicy.FreshFor(FacetKind.PlaylistRevision, TimeSpan.FromHours(24)));
        Assert.Equal(now.AddSeconds(20), ResourcePolicy.ExpiresAt(new PlaylistHeaderValue(NextUpdateAt: now.AddSeconds(20)), now));
        Assert.Equal(now.AddMinutes(5), ResourcePolicy.ExpiresAt(new PlaylistHeaderValue(NextUpdateAt: now.AddSeconds(-1)), now));
    }

    [Fact]
    public void TypedPatch_PreservesOmittedFieldsAndAcceptsZeroFalseAndClear()
    {
        CatalogValue value = new TrackIdentityPatch(Title: FieldChange<string?>.Set("Title"),
            DurationMs: FieldChange<long?>.Set(123), IsExplicit: FieldChange<bool?>.Set(true),
            Image: FieldChange<Image?>.Set(new Image("https://images.test/art"))).Apply(null);
        value = new TrackIdentityPatch(DurationMs: FieldChange<long?>.Set(0),
            IsExplicit: FieldChange<bool?>.Set(false), Image: FieldChange<Image?>.Set(null)).Apply(value);
        var track = Assert.IsType<TrackIdentityValue>(value);
        Assert.Equal("Title", track.Title);
        Assert.Equal(0, track.DurationMs);
        Assert.False(track.IsExplicit);
        Assert.Null(track.Image);
        var seeded = Assert.IsType<TrackIdentityValue>(new TrackIdentityPatch(
            Image: FieldChange<Image?>.Set(new Image("https://images.test/old"))).Apply(value, fillUnknownOnly: true));
        Assert.Null(seeded.Image); // An authoritative clear remains known.
    }

    [Fact]
    public void IndependentFacets_CannotOverwriteEachOthersFields()
    {
        CatalogValue count = new ReplaceFacetPatch(new PlayCountValue(0)).Apply(null);
        CatalogValue identity = new TrackIdentityPatch(Title: FieldChange<string?>.Set("Song")).Apply(null);
        Assert.Equal(0, Assert.IsType<PlayCountValue>(count).Count);
        Assert.Equal("Song", Assert.IsType<TrackIdentityValue>(identity).Title);
        Assert.Throws<ArgumentException>(() => new TrackIdentityPatch().Apply(count));
    }

    [Fact]
    public void OrderedRelationCodec_PreservesDuplicateUrisOccurrenceContextAndEmptySuccess()
    {
        var page = new RelationPageValue(FacetKind.AlbumTracks, "snapshot-7", "head-7", 0, 2, null,
            RelationCoverage.Complete, [new("disc1-1", "spotify:track:x", new(1, 1)),
                new("disc2-1", "spotify:track:x", new(2, 1))]);
        var decoded = Assert.IsType<RelationPageValue>(CatalogPayloadCodec.Decode(page.Facet, CatalogPayloadCodec.Encode(page)));
        Assert.Equal(page.SnapshotId, decoded.SnapshotId);
        Assert.Equal(page.Items, decoded.Items);
        var empty = page with { Total = 0, Items = Array.Empty<CatalogRelationItem>() };
        Assert.Empty(Assert.IsType<RelationPageValue>(CatalogPayloadCodec.Decode(empty.Facet,
            CatalogPayloadCodec.Encode(empty))).Items);
    }

    [Fact]
    public void RelationCodec_RejectsDuplicateOccurrencesAndWrongFacet()
    {
        var malformed = new RelationPageValue(FacetKind.AlbumTracks, "snapshot", null, 0, 2, null,
            RelationCoverage.Complete, [new("same", "spotify:track:x"), new("same", "spotify:track:y")]);
        Assert.Throws<ArgumentException>(() => CatalogPayloadCodec.Decode(malformed.Facet, CatalogPayloadCodec.Encode(malformed)));
        Assert.Throws<System.Text.Json.JsonException>(() => CatalogPayloadCodec.Decode(FacetKind.ShowEpisodes,
            CatalogPayloadCodec.Encode(malformed)));
    }

    [Fact]
    public void Arguments_AreStableAndDoNotAliasSeparatorsNullOrEmpty()
    {
        var encoded = new HashSet<string>
        {
            new ResourceArguments(Cursor: null).ToStorageKey(), new ResourceArguments(Cursor: "").ToStorageKey(),
            new ResourceArguments(Cursor: "a:|b", Filter: "c").ToStorageKey(),
            new ResourceArguments(Cursor: "a", Filter: ":|bc").ToStorageKey(),
        };
        Assert.Equal(4, encoded.Count);
        Assert.Equal(new ResourceArguments(1, 30, "next", "albums").ToStorageKey(),
            new ResourceArguments(1, 30, "next", "albums").ToStorageKey());
    }

    [Theory]
    [InlineData(FacetKind.TrackIdentity, 3600)]
    [InlineData(FacetKind.AlbumDetail, 600)]
    [InlineData(FacetKind.ArtistOverview, 43200)]
    [InlineData(FacetKind.PlayCount, 21600)]
    public void Freshness_IsFacetSpecific(FacetKind facet, int seconds)
        => Assert.Equal(TimeSpan.FromSeconds(seconds), ResourcePolicy.FreshFor(facet));

    [Fact]
    public void ExtensionTtl_IsClampedAndFailuresHaveOneFiniteRetryOwner()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), ResourcePolicy.FreshFor(FacetKind.PlayCount, TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromHours(24), ResourcePolicy.FreshFor(FacetKind.PlayCount, TimeSpan.FromDays(30)));
        Assert.Null(ResourcePolicy.RetryDelay(new(ResourceErrorKind.Transport, "429", 429), 1));
        Assert.Null(ResourcePolicy.RetryDelay(new(ResourceErrorKind.Forbidden, "403", 403), 1));
        Assert.Null(ResourcePolicy.RetryDelay(new(ResourceErrorKind.Decode, "bad bytes"), 1));
        Assert.Null(ResourcePolicy.RetryDelay(new(ResourceErrorKind.Transport, "503", 503), 4));
    }
}
