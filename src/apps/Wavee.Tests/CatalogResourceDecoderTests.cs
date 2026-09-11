using System;
using System.IO;
using System.Linq;
using Google.Protobuf;
using Wavee.Backend.Catalog;
using Wavee.Core.Catalog;
using Xunit;
using Aa = Wavee.Protocol.AudioAttributes;
using Ca = Wavee.Protocol.ContentAgnostic;
using De = Wavee.Protocol.DescriptorExtension;
using Lean = Wavee.Protocol.Lean;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.Tests;

public sealed class CatalogResourceDecoderTests
{
    [Fact]
    public void ReusedDocumentDecoder_PreservesFinitePageAndChildObservationOrder()
    {
        var album = new Lean.LeanAlbum { Gid = Gid(1), Name = "Album" };
        var disc = new Lean.LeanDisc { Number = 1 };
        for (int i = 0; i < 107; i++) disc.Track.Add(new Lean.LeanTrack { Gid = Gid((byte)(i + 2)), Name = "Track " + i });
        album.Disc.Add(disc);
        var payload = album.ToByteString();
        var decode = SpotifyCatalogDecoder.CreateDocumentDecoder((int)Xm.ExtensionKind.AlbumV4, payload);
        foreach (int offset in new[] { 0, 50, 100 })
        {
            var key = new ResourceKey(Scope, "spotify:album:a", FacetKind.AlbumTracks, new(offset, 50));
            var independent = SpotifyCatalogDecoder.Decode(key, payload);
            var shared = decode(key);
            Assert.Equal(CatalogPayloadCodec.Encode(independent.Patch.Apply(null)), CatalogPayloadCodec.Encode(shared.Patch.Apply(null)));
            Assert.Equal(independent.Seeds.Select(seed => seed.Key), shared.Seeds.Select(seed => seed.Key));
            for (int i = 0; i < independent.Seeds.Count; i++)
                Assert.Equal(CatalogPayloadCodec.Encode(independent.Seeds[i].Patch.Apply(null)), CatalogPayloadCodec.Encode(shared.Seeds[i].Patch.Apply(null)));
        }
    }

    [Fact]
    public void NameOnlyArtistCredit_SurvivesSeedAndCodecWithoutInventingAnEntity()
    {
        var track = new Wavee.Core.Track("file", "local:track:file", "Song",
            [new("", "", "Original artist")], new("", "", "OutRun"), 100, false, null);
        var seeds = new System.Collections.Generic.List<CatalogSeed>();
        CatalogDomainSeeds.Track(Scope, track, seeds);
        var patch = Assert.Single(seeds.Where(seed => seed.Key.Facet == FacetKind.TrackIdentity)).Patch;
        var value = Assert.IsType<TrackIdentityValue>(patch.Apply(null, true));
        Assert.Empty(value.ArtistUris!);
        Assert.Equal("Original artist", Assert.Single(value.UnlinkedArtistNames!));
        Assert.DoesNotContain(seeds, seed => seed.Key.Facet == FacetKind.ArtistIdentity);
        var roundTrip = Assert.IsType<TrackIdentityValue>(CatalogPayloadCodec.Decode(FacetKind.TrackIdentity, CatalogPayloadCodec.Encode(value)));
        Assert.Equal(value.UnlinkedArtistNames, roundTrip.UnlinkedArtistNames);
        Assert.Equal("OutRun", roundTrip.UnlinkedAlbumName);
        Assert.Null(roundTrip.AlbumUri);
        var cleared = new TrackIdentityPatch(UnlinkedArtistNames: FieldChange<System.Collections.Generic.IReadOnlyList<string>?>.Set([])).Apply(roundTrip);
        var reseeded = Assert.IsType<TrackIdentityValue>(patch.Apply(cleared, true));
        Assert.Empty(reseeded.UnlinkedArtistNames!);
    }

    static readonly CatalogScope Scope = new("spotify", "me", "en", "US", "premium", 1, false);
    static CatalogDecodedFacet Decode(FacetKind facet, ByteString payload, string uri = "spotify:track:requested")
        => SpotifyCatalogDecoder.Decode(new(Scope, uri, facet), payload);
    static T Value<T>(FacetKind facet, IMessage message) where T : CatalogValue
        => Assert.IsType<T>(Decode(facet, message.ToByteString()).Patch.Apply(null));

    [Fact]
    public void TrackIdentity_PreservesRequestedSubjectAndNormalizesEmbeddedReferences()
    {
        var track = new Lean.LeanTrack { Name = "Real song", Duration = 234000, Explicit = false,
            CanonicalUri = "spotify:track:canonical", Album = new Lean.LeanAlbumRef { Gid = Gid(2), Name = "Album" } };
        track.Artist.Add(new Lean.LeanArtistRef { Gid = Gid(3), Name = "Artist" });
        var decoded = Decode(FacetKind.TrackIdentity, track.ToByteString());
        var value = Assert.IsType<TrackIdentityValue>(decoded.Patch.Apply(null));
        Assert.Equal("Real song", value.Title);
        Assert.Equal(234000, value.DurationMs);
        Assert.False(value.IsExplicit);
        Assert.Equal("spotify:track:canonical", value.CanonicalUri);
        Assert.Contains(decoded.Seeds, seed => seed.Key.Subject == value.AlbumUri && seed.Patch.Apply(null) is AlbumIdentityValue { Name: "Album" });
        Assert.Contains(decoded.Seeds, seed => seed.Key.Subject == Assert.Single(value.ArtistUris!) && seed.Patch.Apply(null) is ArtistIdentityValue { Name: "Artist" });
    }

    [Fact]
    public void TrackIdentity_OmittedDurationPreservesPriorZeroAndFalse()
    {
        var prior = new TrackIdentityPatch(DurationMs: FieldChange<long?>.Set(0), IsExplicit: FieldChange<bool?>.Set(false)).Apply(null);
        var changed = Decode(FacetKind.TrackIdentity, new Lean.LeanTrack { Name = "New" }.ToByteString()).Patch.Apply(prior);
        Assert.Equal(0, Assert.IsType<TrackIdentityValue>(changed).DurationMs);
        Assert.False(((TrackIdentityValue)changed).IsExplicit);
    }

    [Fact]
    public void ImageDecoder_PreservesDefaultAndLargestRenditions()
    {
        var group = new Lean.LeanImageGroup();
        group.Image.Add(new Lean.LeanImage { FileId = Gid(1), Size = 0, Width = 300, Height = 300 });
        group.Image.Add(new Lean.LeanImage { FileId = Gid(2), Size = 2, Width = 1280, Height = 1280 });
        var image = SpotifyCatalogDecoder.ImageOf(group)!;
        Assert.EndsWith(Convert.ToHexStringLower(Gid(1).Span), image.Url);
        Assert.EndsWith(Convert.ToHexStringLower(Gid(2).Span), image.LargestUrl);
        Assert.Equal(300, image.Width);
    }

    [Fact]
    public void Reputation_SkipsUnknownFieldsAndAcceptsExplicitZero()
    {
        using var stream = new MemoryStream();
        using (var writer = new CodedOutputStream(stream, true))
        {
            writer.WriteTag(1, WireFormat.WireType.LengthDelimited); writer.WriteString("unknown");
            writer.WriteTag(2, WireFormat.WireType.Fixed64); writer.WriteFixed64(17);
            writer.WriteTag(3, WireFormat.WireType.Varint); writer.WriteUInt64(0); writer.Flush();
        }
        Assert.Equal(0, Assert.IsType<PlayCountValue>(Decode(FacetKind.PlayCount, ByteString.CopyFrom(stream.ToArray())).Patch.Apply(null)).Count);
        Assert.Throws<ArgumentException>(() => Decode(FacetKind.PlayCount, ByteString.Empty));
        Assert.Throws<InvalidProtocolBufferException>(() => Decode(FacetKind.PlayCount, ByteString.CopyFrom(new byte[] { 24, 128 })));
    }

    [Fact]
    public void Descriptors_PreserveDisplayOrderCapSixAndExplicitEmpty()
    {
        var descriptors = new De.ExtensionDescriptorData();
        for (int i = 0; i < 8; i++) descriptors.Descriptors.Add(new De.ExtensionDescriptor { Text = "raw" + i, DisplayName = "shown" + i });
        Assert.Equal(Enumerable.Range(0, 6).Select(i => "shown" + i), Value<DescriptorsValue>(FacetKind.Descriptors, descriptors).Tags);
        Assert.Empty(Value<DescriptorsValue>(FacetKind.Descriptors, new De.ExtensionDescriptorData()).Tags);
    }

    [Fact]
    public void AudioAttributes_PreserveMusicalKeyAndRejectZeroTempoAsUnknown()
    {
        var message = new Aa.AudioAttributes { Tempo = 123.5, Key = new Aa.MusicalKey { Name = "A minor", Camelot = new Aa.Camelot { Code = "8A", Color = "#123456" } } };
        var audio = Value<AudioAttributesValue>(FacetKind.AudioAttributes, message);
        Assert.Equal(123.5, audio.TempoBpm); Assert.Equal("A minor", audio.MusicalKey); Assert.Equal("8A", audio.CamelotCode);
        Assert.NotNull(audio.CamelotColor);
        message.Tempo = 0; Assert.Null(Value<AudioAttributesValue>(FacetKind.AudioAttributes, message).TempoBpm);
    }

    [Theory]
    [InlineData(2014, 0, 0, "2014", "YEAR")]
    [InlineData(2014, 11, 0, "2014-11", "MONTH")]
    [InlineData(2014, 11, 18, "2014-11-18", "DAY")]
    public void Publishing_UsesCalendarDateAndPrecision(int year, int month, int day, string date, string precision)
    {
        var message = new Ca.PublishingMetadataTrait { Date = new Ca.PublishingMetadataTrait.Types.Date { Year = year, Month = month, Day = day },
            Published = new Ca.PublishingMetadataTrait.Types.Timestamp { Seconds = 1 }, Available = new Ca.PublishingMetadataTrait.Types.Timestamp { Seconds = 2 } };
        message.Copyright.Add("(C) Company"); message.Copyright.Add("(P) Label");
        var value = Value<PublishingValue>(FacetKind.Publishing, message);
        Assert.Equal(date, value.ReleaseDate); Assert.Equal(precision, value.Precision);
        Assert.Contains("Company", value.Copyright); Assert.Contains("Label", value.Copyright);
    }

    [Fact]
    public void VisualIdentity_PreservesAllImageKeysAndFiveColorRoles()
    {
        var identity = new Ca.VisualIdentity { Colors = new Ca.ColorSet { Base = new Ca.ColorScheme {
            BackgroundBase = Color(1), BackgroundTintedBase = Color(2), TextBase = Color(3), TextSubdued = Color(4), TextBrightAccent = Color(5) } } };
        identity.Images.Add(new Ca.ImageEntry { Image = new Ca.ImageRef { Url = "image:a" } });
        identity.Images.Add(new Ca.ImageEntry { Image = new Ca.ImageRef { Url = "image:b" } });
        var value = Value<VisualIdentityValue>(FacetKind.VisualIdentity, new Ca.VisualIdentityTrait { VisualIdentity = identity });
        Assert.Equal(new[] { "image:a", "image:b" }, value.ImageUris);
        Assert.NotNull(value.Background); Assert.NotNull(value.BackgroundTinted); Assert.NotNull(value.Text);
        Assert.NotNull(value.TextSubdued); Assert.NotNull(value.Accent);
    }

    [Fact]
    public void VideoAssociation_HoldsCounterpartWithoutRequiringAnIdentityRow()
    {
        var value = Value<VideoAssociationValue>(FacetKind.VideoAssociation, new Xm.VideoAssociations { Association = new Xm.Association { AssociatedUri = "spotify:track:video" } });
        Assert.True(value.Association.HasVideo);
        Assert.Equal("spotify:track:video", value.Association.CounterpartUri);
        Assert.Equal(default, value.Association.FetchedAt);
    }

    static ByteString Gid(byte value) => ByteString.CopyFrom(Enumerable.Repeat(value, 16).ToArray());
    static Ca.Rgba Color(uint value) => new() { R = value, G = value, B = value, A = 255 };
}
