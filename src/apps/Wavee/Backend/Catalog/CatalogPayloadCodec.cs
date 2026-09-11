using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wavee.Core.Catalog;

namespace Wavee.Backend.Catalog;

/// <summary>Explicit, AOT-safe v1 payload codec shared by admission accounting and SQLite.</summary>
public static class CatalogPayloadCodec
{
    public const int Version = 1;
    public static int MeasureObservations(System.Collections.Generic.IReadOnlyList<CatalogObservation> observations)
    {
        int bytes = 0;
        foreach (var observation in observations)
        {
            var key = observation.Key;
            bytes = checked(bytes + 256 + Encode(observation.Patch.Apply(null, observation.FillUnknownOnly)).Length
                + System.Text.Encoding.UTF8.GetByteCount(key.Subject + key.Arguments.ToStorageKey()
                    + key.Scope.Provider + key.Scope.ProviderAccount + key.Scope.Market + key.Scope.Locale + key.Scope.Catalogue + key.Scope.StorageAccount));
        }
        return bytes;
    }
    public static byte[] EncodeRelationContext(RelationContext context)
        => JsonSerializer.SerializeToUtf8Bytes(context, CatalogJson.Default.RelationContext);
    public static RelationContext DecodeRelationContext(ReadOnlySpan<byte> bytes)
        => JsonSerializer.Deserialize(bytes, CatalogJson.Default.RelationContext)
            ?? throw new JsonException("Relation context was null.");
    public static byte[] EncodeStrings(string[] values)
        => JsonSerializer.SerializeToUtf8Bytes(values, CatalogJson.Default.StringArray);
    public static string[] DecodeStrings(ReadOnlySpan<byte> bytes)
        => JsonSerializer.Deserialize(bytes, CatalogJson.Default.StringArray)
            ?? throw new JsonException("String array was null.");
    public static byte[] Encode(CatalogValue value) => value switch
    {
        TrackIdentityValue x => JsonSerializer.SerializeToUtf8Bytes(x, CatalogJson.Default.TrackIdentityValue),
        EpisodeIdentityValue x => JsonSerializer.SerializeToUtf8Bytes(x, CatalogJson.Default.EpisodeIdentityValue),
        AlbumIdentityValue x => JsonSerializer.SerializeToUtf8Bytes(x, CatalogJson.Default.AlbumIdentityValue),
        ArtistIdentityValue x => JsonSerializer.SerializeToUtf8Bytes(x, CatalogJson.Default.ArtistIdentityValue),
        PlaylistHeaderValue x => JsonSerializer.SerializeToUtf8Bytes(x, CatalogJson.Default.PlaylistHeaderValue),
        ShowIdentityValue x => JsonSerializer.SerializeToUtf8Bytes(x, CatalogJson.Default.ShowIdentityValue),
        UserIdentityValue x => JsonSerializer.SerializeToUtf8Bytes(x, CatalogJson.Default.UserIdentityValue),
        PlayCountValue x => JsonSerializer.SerializeToUtf8Bytes(x, CatalogJson.Default.PlayCountValue),
        DescriptorsValue x => JsonSerializer.SerializeToUtf8Bytes(x, CatalogJson.Default.DescriptorsValue),
        AudioAttributesValue x => JsonSerializer.SerializeToUtf8Bytes(x, CatalogJson.Default.AudioAttributesValue),
        PublishingValue x => JsonSerializer.SerializeToUtf8Bytes(x, CatalogJson.Default.PublishingValue),
        AvailabilityValue x => JsonSerializer.SerializeToUtf8Bytes(x, CatalogJson.Default.AvailabilityValue),
        VideoAssociationValue x => JsonSerializer.SerializeToUtf8Bytes(x, CatalogJson.Default.VideoAssociationValue),
        VisualIdentityValue x => JsonSerializer.SerializeToUtf8Bytes(x, CatalogJson.Default.VisualIdentityValue),
        AlbumDetailValue x => JsonSerializer.SerializeToUtf8Bytes(x, CatalogJson.Default.AlbumDetailValue),
        ArtistOverviewValue x => JsonSerializer.SerializeToUtf8Bytes(x, CatalogJson.Default.ArtistOverviewValue),
        EpisodeDetailValue x => JsonSerializer.SerializeToUtf8Bytes(x, CatalogJson.Default.EpisodeDetailValue),
        PlaylistRevisionValue x => JsonSerializer.SerializeToUtf8Bytes(x, CatalogJson.Default.PlaylistRevisionValue),
        ExtensionDocumentValue x => JsonSerializer.SerializeToUtf8Bytes(x, CatalogJson.Default.ExtensionDocumentValue),
        RelationPageValue x => JsonSerializer.SerializeToUtf8Bytes(x, CatalogJson.Default.RelationPageValue),
        CatalogDocumentValue x => JsonSerializer.SerializeToUtf8Bytes(x, CatalogJson.Default.CatalogDocumentValue),
        _ => throw new ArgumentException("Unregistered catalog payload.", nameof(value)),
    };

    public static CatalogValue Decode(FacetKind facet, ReadOnlySpan<byte> bytes)
    {
        CatalogValue? value = facet switch
        {
            FacetKind.TrackIdentity => JsonSerializer.Deserialize(bytes, CatalogJson.Default.TrackIdentityValue),
            FacetKind.EpisodeIdentity => JsonSerializer.Deserialize(bytes, CatalogJson.Default.EpisodeIdentityValue),
            FacetKind.AlbumIdentity => JsonSerializer.Deserialize(bytes, CatalogJson.Default.AlbumIdentityValue),
            FacetKind.ArtistIdentity => JsonSerializer.Deserialize(bytes, CatalogJson.Default.ArtistIdentityValue),
            FacetKind.PlaylistHeader => JsonSerializer.Deserialize(bytes, CatalogJson.Default.PlaylistHeaderValue),
            FacetKind.ShowIdentity => JsonSerializer.Deserialize(bytes, CatalogJson.Default.ShowIdentityValue),
            FacetKind.UserIdentity => JsonSerializer.Deserialize(bytes, CatalogJson.Default.UserIdentityValue),
            FacetKind.PlayCount => JsonSerializer.Deserialize(bytes, CatalogJson.Default.PlayCountValue),
            FacetKind.Descriptors => JsonSerializer.Deserialize(bytes, CatalogJson.Default.DescriptorsValue),
            FacetKind.AudioAttributes => JsonSerializer.Deserialize(bytes, CatalogJson.Default.AudioAttributesValue),
            FacetKind.Publishing => JsonSerializer.Deserialize(bytes, CatalogJson.Default.PublishingValue),
            FacetKind.Availability => JsonSerializer.Deserialize(bytes, CatalogJson.Default.AvailabilityValue),
            FacetKind.VideoAssociation => JsonSerializer.Deserialize(bytes, CatalogJson.Default.VideoAssociationValue),
            FacetKind.VisualIdentity => JsonSerializer.Deserialize(bytes, CatalogJson.Default.VisualIdentityValue),
            FacetKind.AlbumDetail => JsonSerializer.Deserialize(bytes, CatalogJson.Default.AlbumDetailValue),
            FacetKind.ArtistOverview => JsonSerializer.Deserialize(bytes, CatalogJson.Default.ArtistOverviewValue),
            FacetKind.EpisodeDetail => JsonSerializer.Deserialize(bytes, CatalogJson.Default.EpisodeDetailValue),
            FacetKind.PlaylistRevision => JsonSerializer.Deserialize(bytes, CatalogJson.Default.PlaylistRevisionValue),
            FacetKind.ExtensionDocument => JsonSerializer.Deserialize(bytes, CatalogJson.Default.ExtensionDocumentValue),
            FacetKind.AlbumTracks or FacetKind.ArtistDiscography or FacetKind.ArtistPopular
                or FacetKind.ArtistAppearsOn or FacetKind.ShowEpisodes or FacetKind.AlbumVersions
                or FacetKind.ArtistRelated => JsonSerializer.Deserialize(bytes, CatalogJson.Default.RelationPageValue),
            FacetKind.Home or FacetKind.Search or FacetKind.HomeSection or FacetKind.SearchSuggestions => JsonSerializer.Deserialize(bytes, CatalogJson.Default.CatalogDocumentValue),
            _ => throw new ArgumentOutOfRangeException(nameof(facet)),
        };
        if (value is null || value.Facet != facet) throw new JsonException("Catalog payload does not own its facet.");
        CatalogValueRules.Validate(value);
        return value;
    }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(TrackIdentityValue))]
[JsonSerializable(typeof(EpisodeIdentityValue))]
[JsonSerializable(typeof(AlbumIdentityValue))]
[JsonSerializable(typeof(ArtistIdentityValue))]
[JsonSerializable(typeof(PlaylistHeaderValue))]
[JsonSerializable(typeof(ShowIdentityValue))]
[JsonSerializable(typeof(UserIdentityValue))]
[JsonSerializable(typeof(PlayCountValue))]
[JsonSerializable(typeof(DescriptorsValue))]
[JsonSerializable(typeof(AudioAttributesValue))]
[JsonSerializable(typeof(PublishingValue))]
[JsonSerializable(typeof(AvailabilityValue))]
[JsonSerializable(typeof(VideoAssociationValue))]
[JsonSerializable(typeof(VisualIdentityValue))]
[JsonSerializable(typeof(AlbumDetailValue))]
[JsonSerializable(typeof(ArtistOverviewValue))]
[JsonSerializable(typeof(EpisodeDetailValue))]
[JsonSerializable(typeof(PlaylistRevisionValue))]
[JsonSerializable(typeof(ExtensionDocumentValue))]
[JsonSerializable(typeof(RelationPageValue))]
[JsonSerializable(typeof(CatalogDocumentValue))]
[JsonSerializable(typeof(RelationContext))]
[JsonSerializable(typeof(string[]))]
internal partial class CatalogJson : JsonSerializerContext;
