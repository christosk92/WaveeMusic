using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using Google.Protobuf;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Wavee.Core.Catalog;
using Lean = Wavee.Protocol.Lean;
using Xm = Wavee.Protocol.ExtendedMetadata;
using Ca = Wavee.Protocol.ContentAgnostic;

namespace Wavee.Backend.Catalog;

public sealed record CatalogDecodedFacet(CatalogPatch Patch, IReadOnlyList<CatalogSeed> Seeds);

/// <summary>Pure Spotify bytes to owned facet fields. No store, clock, request policy or entity merge.</summary>
public static class SpotifyCatalogDecoder
{
    public static Xm.ExtensionKind? ExtensionFor(ResourceKey key)
        => key.Facet == FacetKind.ExtensionDocument
            ? int.TryParse(key.Arguments.Filter, NumberStyles.None, CultureInfo.InvariantCulture, out int kind) && kind > 0
                ? (Xm.ExtensionKind)kind : null
            : ExtensionFor(key.Facet);
    /// <summary>The entity kind an extension document is defined FOR. The wire answers a whole extended-metadata POST
    /// with HTTP 400 when one entity/kind pair is nonsense (a <c>ShowV4</c> on an album uri), taking every other subject
    /// in the batch down with it — so a mismatch is decided here, per key, and never sent. Null = the kind is a trait
    /// whose subject rules are not pinned down; it goes to the wire as asked.</summary>
    public static EntityKind? SubjectKindFor(Xm.ExtensionKind kind) => kind switch
    {
        Xm.ExtensionKind.TrackV4 => EntityKind.Track,
        Xm.ExtensionKind.EpisodeV4 => EntityKind.Episode,
        Xm.ExtensionKind.AlbumV4 => EntityKind.Album,
        Xm.ExtensionKind.ArtistV4 => EntityKind.Artist,
        Xm.ExtensionKind.ShowV4 => EntityKind.Show,
        Xm.ExtensionKind.UserProfile => EntityKind.User,
        _ => null,
    };

    /// <summary>True when <paramref name="key"/>'s extension document is defined for its subject's kind (or unpinned).</summary>
    public static bool SubjectMatches(ResourceKey key)
        => ExtensionFor(key) is not { } kind || SubjectKindFor(kind) is not { } required || EntityUri.KindOf(key.Subject) == required;

    public static Xm.ExtensionKind? ExtensionFor(FacetKind facet) => facet switch
    {
        FacetKind.TrackIdentity or FacetKind.Availability => Xm.ExtensionKind.TrackV4,
        FacetKind.EpisodeIdentity or FacetKind.EpisodeDetail => Xm.ExtensionKind.EpisodeV4,
        FacetKind.AlbumIdentity or FacetKind.AlbumTracks => Xm.ExtensionKind.AlbumV4,
        FacetKind.ArtistIdentity or FacetKind.ArtistDiscography or FacetKind.ArtistAppearsOn => Xm.ExtensionKind.ArtistV4,
        FacetKind.ShowIdentity or FacetKind.ShowEpisodes => Xm.ExtensionKind.ShowV4,
        FacetKind.UserIdentity => Xm.ExtensionKind.UserProfile,
        FacetKind.PlayCount => Xm.ExtensionKind.OnPlatformReputationTrait,
        FacetKind.Descriptors => Xm.ExtensionKind.TrackDescriptor,
        FacetKind.AudioAttributes => Xm.ExtensionKind.AudioAttributesV2,
        FacetKind.Publishing => Xm.ExtensionKind.PublishingMetadataTrait,
        FacetKind.VideoAssociation => Xm.ExtensionKind.VideoAssociations,
        FacetKind.VisualIdentity => Xm.ExtensionKind.VisualIdentityTrait,
        _ => null,
    };

    public static CatalogDecodedFacet Decode(ResourceKey key, ByteString payload, string? sourceRevision = null)
        => DecodeCore(key, payload, sourceRevision, null);

    /// <summary>One immutable transport body may supply several finite facets/pages. Decode its protobuf once,
    /// while retaining exactly the same page keys and child-observation order as individual reads.
    /// <paramref name="sourceRevision"/> is the ONE wire ETag for this document (identical for every key sharing
    /// it) — <see cref="FacetKind.ExtensionDocument"/> stores it as the pointer CatalogExtensionReader compares
    /// against the retained transport's own ETag; omitting it here left that pointer permanently null and made
    /// every shared-document read fail as "changed during read".</summary>
    public static Func<ResourceKey, CatalogDecodedFacet> CreateDocumentDecoder(int extensionKind, ByteString payload, string? sourceRevision = null)
    {
        IMessage? decoded = (Xm.ExtensionKind)extensionKind switch
        {
            Xm.ExtensionKind.TrackV4 => Parse(Lean.LeanTrack.Parser, payload),
            Xm.ExtensionKind.EpisodeV4 => Parse(Lean.LeanEpisode.Parser, payload),
            Xm.ExtensionKind.AlbumV4 => Parse(Lean.LeanAlbum.Parser, payload),
            Xm.ExtensionKind.ArtistV4 => Parse(Lean.LeanArtist.Parser, payload),
            Xm.ExtensionKind.ShowV4 => Parse(Lean.LeanShow.Parser, payload),
            _ => null,
        };
        return key => DecodeCore(key, payload, sourceRevision, decoded);
    }

    static CatalogDecodedFacet DecodeCore(ResourceKey key, ByteString payload, string? sourceRevision, IMessage? document)
    {
        T Model<T>(MessageParser<T> parser) where T : IMessage<T> => document is T cached ? cached : Parse(parser, payload);
        var seeds = new List<CatalogSeed>();
        CatalogPatch patch = key.Facet switch
        {
            FacetKind.ExtensionDocument => new ReplaceFacetPatch(new ExtensionDocumentValue(
                (int)(ExtensionFor(key) ?? throw new ArgumentException("Missing extension kind.")), sourceRevision)),
            FacetKind.TrackIdentity => Track(key, Model(Lean.LeanTrack.Parser), seeds),
            FacetKind.EpisodeIdentity => Episode(key, Model(Lean.LeanEpisode.Parser), seeds),
            FacetKind.AlbumIdentity => Album(key, Model(Lean.LeanAlbum.Parser), seeds),
            FacetKind.ArtistIdentity => Artist(Model(Lean.LeanArtist.Parser)),
            FacetKind.PlaylistHeader => Playlist(Parse(Xm.ListMetadataV2.Parser, payload)),
            FacetKind.ShowIdentity => Show(Model(Lean.LeanShow.Parser)),
            FacetKind.UserIdentity => User(payload),
            FacetKind.AlbumTracks => AlbumTracks(key, Model(Lean.LeanAlbum.Parser), payload, sourceRevision, seeds),
            FacetKind.ArtistDiscography or FacetKind.ArtistAppearsOn => ArtistAlbums(key,
                Model(Lean.LeanArtist.Parser), payload, sourceRevision, seeds),
            FacetKind.ShowEpisodes => ShowEpisodes(key, Model(Lean.LeanShow.Parser), payload, sourceRevision, seeds),
            FacetKind.Availability => new ReplaceFacetPatch(Availability(Model(Lean.LeanTrack.Parser))
                ?? throw new ArgumentException("TrackV4 did not carry an availability verdict.")),
            FacetKind.EpisodeDetail => new ReplaceFacetPatch(new EpisodeDetailValue(Model(Lean.LeanEpisode.Parser).Description)),
            FacetKind.PlayCount => new ReplaceFacetPatch(PlayCount(payload)),
            FacetKind.Descriptors => new ReplaceFacetPatch(Descriptors(payload)),
            FacetKind.AudioAttributes => new ReplaceFacetPatch(Audio(payload)),
            FacetKind.Publishing => new ReplaceFacetPatch(Publishing(payload)),
            FacetKind.VideoAssociation => new ReplaceFacetPatch(Video(key.Subject, payload)),
            FacetKind.VisualIdentity => new ReplaceFacetPatch(Visual(payload)),
            _ => throw new NotSupportedException($"Facet {key.Facet} has no extended-metadata decoder."),
        };
        CatalogValueRules.Validate(patch.Apply(null));
        return new(patch, seeds);
    }

    static T Parse<T>(MessageParser<T> parser, ByteString payload) where T : IMessage<T>
        => parser.WithDiscardUnknownFields(true).ParseFrom(payload);

    public static TrackIdentityPatch Track(ResourceKey key, Lean.LeanTrack track, List<CatalogSeed> seeds)
    {
        var artists = ArtistRefs(key.Scope, track.Artist, seeds);
        string? albumUri = null;
        Image? image = null;
        if (track.Album is { Gid.Length: > 0 } album)
        {
            albumUri = UriOf("album", album.Gid);
            image = ImageOf(album.CoverGroup);
            seeds.Add(new(new(key.Scope, albumUri, FacetKind.AlbumIdentity), new AlbumIdentityPatch(
                Name: Field(album.HasName, album.Name), Cover: Field(album.CoverGroup is not null, image),
                Year: Field<int?>(album.Date?.HasYear == true, album.Date?.Year))));
        }
        var availability = Availability(track);
        if (availability is not null)
            seeds.Add(new(key with { Facet = FacetKind.Availability, Arguments = default }, new ReplaceFacetPatch(availability)));
        var isrc = track.ExternalId.FirstOrDefault(id => string.Equals(id.Type, "isrc", StringComparison.OrdinalIgnoreCase));
        return new(Title: Field(track.HasName, track.Name), ArtistUris: Field<IReadOnlyList<string>?>(true, artists),
            AlbumUri: Field(track.Album is not null, albumUri), DurationMs: Field<long?>(track.HasDuration, track.Duration),
            IsExplicit: Field<bool?>(track.HasExplicit, track.Explicit), Image: Field(track.Album?.CoverGroup is not null, image),
            Isrc: Field(isrc is not null, isrc?.Id), Year: Field<int?>(track.Album?.Date?.HasYear == true, track.Album?.Date?.Year),
            CanonicalUri: Field(track.HasCanonicalUri, CanonicalUri(track, key.Subject)),
            UnlinkedArtistNames: Field<IReadOnlyList<string>?>(true,
                track.Artist.Where(artist => artist.Gid.IsEmpty && artist.HasName && artist.Name.Length > 0).Select(artist => artist.Name).ToArray()),
            UnlinkedAlbumName: Field(track.Album is not null,
                track.Album is { Gid.IsEmpty: true, HasName: true } unlinked ? unlinked.Name : null));
    }

    public static AlbumIdentityPatch Album(ResourceKey key, Lean.LeanAlbum album, List<CatalogSeed> seeds)
        => new(Name: Field(album.HasName, album.Name), Cover: Field(album.CoverGroup is not null, ImageOf(album.CoverGroup)),
            ArtistUris: Field<IReadOnlyList<string>?>(true, ArtistRefs(key.Scope, album.Artist, seeds)),
            Year: Field<int?>(album.Date?.HasYear == true, album.Date?.Year),
            TrackCount: Field<int?>(album.Disc.Count > 0, album.Disc.Sum(disc => disc.Track.Count)),
            Kind: Field(album.HasType, album.Type switch { 2 => AlbumKind.Single, 3 => AlbumKind.Compilation, 4 => AlbumKind.EP, _ => AlbumKind.Album }));

    static ArtistIdentityPatch Artist(Lean.LeanArtist artist)
        => new(Field(artist.HasName, artist.Name), Field(artist.PortraitGroup is not null, ImageOf(artist.PortraitGroup)));

    static EpisodeIdentityPatch Episode(ResourceKey key, Lean.LeanEpisode episode, List<CatalogSeed> seeds)
    {
        string? showUri = episode.Show is { Gid.Length: > 0 } show ? UriOf("show", show.Gid) : null;
        if (showUri is not null)
            seeds.Add(new(new(key.Scope, showUri, FacetKind.ShowIdentity), new ShowIdentityPatch(
                Name: Field(episode.Show.HasName, episode.Show.Name))));
        if (episode.HasDescription)
            seeds.Add(new(key with { Facet = FacetKind.EpisodeDetail, Arguments = default },
                new ReplaceFacetPatch(new EpisodeDetailValue(episode.Description))));
        return new(Field(episode.HasName, episode.Name), Field(episode.Show is not null, showUri),
            Field<long?>(episode.HasDuration, episode.Duration), Field(episode.CoverImage is not null, ImageOf(episode.CoverImage)),
            Field<DateTimeOffset?>(episode.PublishTime is not null, Date(episode.PublishTime)),
            ShowName: Field(episode.Show is not null, showUri is null && episode.Show?.HasName == true ? episode.Show.Name : null));
    }

    static ShowIdentityPatch Show(Lean.LeanShow show) => new(Field(show.HasName, show.Name),
        Field(show.HasPublisher, show.Publisher), Field(show.CoverImage is not null, ImageOf(show.CoverImage)),
        Field(show.HasDescription, show.Description), Field<int?>(show.Episode.Count > 0, show.Episode.Count));

    static ReplaceFacetPatch User(ByteString payload)
    {
        var profile = UserProfilePayloadDecoder.Decode(payload.Span)
            ?? throw new ArgumentException("User-profile extension omitted its profile fields.");
        return new(new UserIdentityValue(profile.Name, profile.ImageUrl is null ? null : new Image(profile.ImageUrl)));
    }

    public static PlaylistHeaderPatch Playlist(Xm.ListMetadataV2 playlist)
    {
        // proto3 scalar defaults are authoritative on this complete header response. It owns no permission/membership fields.
        string? cover = playlist.Images?.Variant.FirstOrDefault(image => image.Format is ("default" or "large") && image.Url.Length > 0)?.Url
            ?? playlist.Images?.Variant.FirstOrDefault(image => image.Url.Length > 0)?.Url;
        return new(Name: FieldChange<string?>.Set(playlist.Name), Description: FieldChange<string?>.Set(playlist.Description),
            Cover: Field(playlist.Images is not null, cover is null ? null : new Image(cover)),
            OwnerName: FieldChange<string?>.Set(playlist.Source));
    }

    static ReplaceFacetPatch AlbumTracks(ResourceKey key, Lean.LeanAlbum album, ByteString payload,
        string? revision, List<CatalogSeed> seeds)
    {
        if (album.Gid.IsEmpty) throw new ArgumentException("AlbumV4 relation has no owning album identity.");
        seeds.Add(new(key with { Facet = FacetKind.AlbumIdentity, Arguments = default }, Album(key, album, seeds)));
        var items = new List<CatalogRelationItem>();
        for (int d = 0; d < album.Disc.Count; d++)
        {
            var disc = album.Disc[d];
            for (int t = 0; t < disc.Track.Count; t++)
            {
                var track = disc.Track[t];
                var uri = UriOf("track", track.Gid);
                int discNumber = track.HasDiscNumber ? track.DiscNumber : disc.HasNumber ? disc.Number : d + 1;
                int trackNumber = track.HasNumber ? track.Number : t + 1;
                items.Add(new($"disc:{d}:track:{t}", uri, new(discNumber, trackNumber)));
                if (!InWindow(key, items.Count - 1)) continue;
                var trackKey = new ResourceKey(key.Scope, uri, FacetKind.TrackIdentity);
                var patch = Track(trackKey, track, seeds) with
                {
                    AlbumUri = FieldChange<string?>.Set(key.Subject),
                    Image = Field(album.CoverGroup is not null, ImageOf(album.CoverGroup)),
                    ArtistUris = track.Artist.Count > 0 ? Field<IReadOnlyList<string>?>(true, ArtistRefs(key.Scope, track.Artist, seeds))
                        : Field<IReadOnlyList<string>?>(album.Artist.Count > 0, ArtistRefs(key.Scope, album.Artist, seeds)),
                };
                seeds.Add(new(trackKey, patch));
            }
        }
        return Page(key, payload, revision, items);
    }

    static ReplaceFacetPatch ArtistAlbums(ResourceKey key, Lean.LeanArtist artist, ByteString payload,
        string? revision, List<CatalogSeed> seeds)
    {
        if (artist.Gid.IsEmpty) throw new ArgumentException("ArtistV4 relation has no owning artist identity.");
        seeds.Add(new(key with { Facet = FacetKind.ArtistIdentity, Arguments = default }, Artist(artist)));
        var items = new List<CatalogRelationItem>();
        void Add(IEnumerable<Lean.LeanAlbumGroup> groups, AlbumKind kind, string groupName)
        {
            int position = 0;
            foreach (var group in groups)
            {
                if (group.Album.Count == 0) throw new ArgumentException("Discography group omitted its representative release.");
                var album = group.Album[0];
                string uri = UriOf("album", album.Gid);
                items.Add(new(groupName + ":" + position++, uri));
                if (!InWindow(key, items.Count - 1)) continue;
                seeds.Add(new(new(key.Scope, uri, FacetKind.AlbumIdentity), new AlbumIdentityPatch(
                    Name: Field(album.HasName, album.Name), Cover: Field(album.CoverGroup is not null, ImageOf(album.CoverGroup)),
                    Year: Field<int?>(album.Date?.HasYear == true, album.Date?.Year), Kind: FieldChange<AlbumKind>.Set(kind))));
            }
        }
        string filter = key.Arguments.Filter?.ToLowerInvariant() ?? "all";
        if (key.Facet == FacetKind.ArtistAppearsOn) Add(artist.AppearsOnGroup, AlbumKind.Album, "appears");
        else
        {
            if (filter is "all" or "albums") Add(artist.AlbumGroup, AlbumKind.Album, "album");
            if (filter is "all" or "singles") Add(artist.SingleGroup, AlbumKind.Single, "single");
            if (filter is "all" or "compilations") Add(artist.CompilationGroup, AlbumKind.Compilation, "compilation");
            if (filter is not ("all" or "albums" or "singles" or "compilations")) throw new ArgumentException("Unknown discography facet.");
        }
        return Page(key, payload, revision, items);
    }

    static ReplaceFacetPatch ShowEpisodes(ResourceKey key, Lean.LeanShow show, ByteString payload,
        string? revision, List<CatalogSeed> seeds)
    {
        if (show.Gid.IsEmpty) throw new ArgumentException("ShowV4 relation has no owning show identity.");
        seeds.Add(new(key with { Facet = FacetKind.ShowIdentity, Arguments = default }, Show(show)));
        var items = show.Episode.Select((episode, index) => new CatalogRelationItem("episode:" + index,
            UriOf("episode", episode.Gid))).ToArray();
        return Page(key, payload, revision, items);
    }

    static ReplaceFacetPatch Page(ResourceKey key, ByteString payload, string? revision, IReadOnlyList<CatalogRelationItem> items)
    {
        int offset = Math.Clamp(key.Arguments.Offset, 0, items.Count);
        int count = key.Arguments.Limit > 0 ? Math.Min(key.Arguments.Limit, items.Count - offset) : items.Count - offset;
        var page = items.Skip(offset).Take(count).ToArray();
        return new(new RelationPageValue(key.Facet, Convert.ToHexStringLower(SHA256.HashData(payload.Span)), revision,
            offset, items.Count, offset + count < items.Count ? (offset + count).ToString(CultureInfo.InvariantCulture) : null,
            offset == 0 && count == items.Count ? RelationCoverage.Complete : RelationCoverage.Partial, page));
    }

    static IReadOnlyList<string> ArtistRefs(CatalogScope scope, IEnumerable<Lean.LeanArtistRef> artists, List<CatalogSeed> seeds)
    {
        var uris = new List<string>();
        foreach (var artist in artists)
        {
            if (artist.Gid.IsEmpty) continue;
            string uri = UriOf("artist", artist.Gid);
            uris.Add(uri);
            seeds.Add(new(new(scope, uri, FacetKind.ArtistIdentity), new ArtistIdentityPatch(Name: Field(artist.HasName, artist.Name))));
        }
        return uris;
    }

    public static AvailabilityValue? Availability(Lean.LeanTrack track)
    {
        Core.Availability? verdict = track.File.Count > 0 || track.Alternative.Any(alternative => alternative.File.Count > 0)
            ? Core.Availability.Playable : track.HasEarliestLiveTimestamp || track.Restriction.Count > 0 ? Core.Availability.Unavailable : null;
        return verdict is { } known ? new(known, track.HasEarliestLiveTimestamp && track.EarliestLiveTimestamp > 0
            ? DateTimeOffset.FromUnixTimeSeconds(track.EarliestLiveTimestamp) : null) : null;
    }
    public static string? CanonicalUri(Lean.LeanTrack track, string self)
    {
        if (!track.HasCanonicalUri || track.CanonicalUri.Length == 0) return null;
        string? uri = track.CanonicalUri.StartsWith("spotify:", StringComparison.Ordinal) ? track.CanonicalUri
            : track.CanonicalUri.Length == 22 ? "spotify:track:" + track.CanonicalUri : null;
        return uri == self ? null : uri;
    }

    static PlayCountValue PlayCount(ByteString payload)
    {
        var input = payload.CreateCodedInput();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (tag == 24)
            {
                ulong count = input.ReadUInt64();
                if (count > long.MaxValue) throw new ArgumentException("Play count exceeds its domain.");
                return new((long)count); // A present zero is a real answer.
            }
            input.SkipLastField();
        }
        throw new ArgumentException("The reputation response omitted its play-count field.");
    }
    static DescriptorsValue Descriptors(ByteString payload)
    {
        var message = Parse(Wavee.Protocol.DescriptorExtension.ExtensionDescriptorData.Parser, payload);
        return new(message.Descriptors.Select(descriptor => descriptor.DisplayName.Length > 0
            ? descriptor.DisplayName : descriptor.Text).Where(text => text.Length > 0).Take(6).ToArray());
    }
    static AudioAttributesValue Audio(ByteString payload)
    {
        var audio = Parse(Wavee.Protocol.AudioAttributes.AudioAttributes.Parser, payload);
        return new(audio.Tempo > 0 ? audio.Tempo : null, EmptyNull(audio.Key?.Name), EmptyNull(audio.Key?.Camelot?.Code),
            SpotifyColor.FromHex(audio.Key?.Camelot?.Color));
    }
    static PublishingValue Publishing(ByteString payload)
    {
        var publishing = Parse(Ca.PublishingMetadataTrait.Parser, payload);
        var date = publishing.Date;
        string? text = null, precision = null;
        if (date is { Year: > 0 and <= 9999 })
        {
            text = date.Year.ToString("D4", CultureInfo.InvariantCulture); precision = "YEAR";
            if (date.Month is >= 1 and <= 12)
            {
                text += "-" + date.Month.ToString("D2", CultureInfo.InvariantCulture); precision = "MONTH";
                if (date.Day >= 1 && date.Day <= DateTime.DaysInMonth(date.Year, date.Month))
                { text += "-" + date.Day.ToString("D2", CultureInfo.InvariantCulture); precision = "DAY"; }
            }
        }
        return new(SpotifyExportMapper.JoinCopyrightLines(publishing.Copyright), text, precision);
    }
    static VideoAssociationValue Video(string uri, ByteString payload)
    {
        var association = Parse(Xm.VideoAssociations.Parser, payload).Association;
        var files = association?.Files?.File.Where(file => !file.FileId.IsEmpty).Select(file => new VideoFileRef(
            Convert.ToHexStringLower(file.FileId.Span), file.Variant, file.Width, file.Height)).ToArray() ?? [];
        string? counterpart = association?.HasAssociatedUri == true ? association.AssociatedUri : null;
        // The containing resource owns freshness; legacy domain transport stamps remain empty in this payload.
        return new(new VideoAssociation(uri, files.Length > 0 || !string.IsNullOrEmpty(counterpart), counterpart,
            files, null, default, 0));
    }
    static VisualIdentityValue Visual(ByteString payload)
    {
        var identity = Parse(Ca.VisualIdentityTrait.Parser, payload).VisualIdentity;
        var images = identity?.Images.Select(image => image.Image?.Url).Where(url => !string.IsNullOrEmpty(url))
            .Select(url => url!).ToArray() ?? [];
        var scheme = identity?.Colors?.Base;
        return new(images.FirstOrDefault() ?? "", Pack(scheme?.BackgroundBase), Pack(scheme?.TextBase), Pack(scheme?.TextBrightAccent))
        { ImageUris = images, BackgroundTinted = Pack(scheme?.BackgroundTintedBase), TextSubdued = Pack(scheme?.TextSubdued) };
    }
    static uint? Pack(Ca.Rgba? color) => color is null ? null : SpotifyColor.Pack(color.R, color.G, color.B, color.A);
    static string? EmptyNull(string? value) => string.IsNullOrEmpty(value) ? null : value;
    static FieldChange<string?> Field(bool present, string? value) => new(present, value);
    static FieldChange<T> Field<T>(bool present, T value) => new(present, value);
    static bool InWindow(ResourceKey key, int index) => index >= Math.Max(0, key.Arguments.Offset)
        && (key.Arguments.Limit <= 0 || index < (long)Math.Max(0, key.Arguments.Offset) + key.Arguments.Limit);
    static string UriOf(string kind, ByteString gid) => gid.Length == 16 ? "spotify:" + kind + ":" + Base62.Encode(gid.Span)
        : throw new ArgumentException("Catalog reference has no complete 16-byte identity.");
    static DateTimeOffset? Date(Lean.LeanDate? date)
    {
        if (date is not { Year: > 0 and <= 9999 }) return null;
        int month = date.HasMonth ? Math.Clamp(date.Month, 1, 12) : 1;
        int day = date.HasDay ? Math.Clamp(date.Day, 1, DateTime.DaysInMonth(date.Year, month)) : 1;
        return new(date.Year, month, day, 0, 0, 0, TimeSpan.Zero);
    }
    public static Image? ImageOf(Lean.LeanImageGroup? group)
    {
        if (group is null) return null;
        var images = group.Image.Where(image => !image.FileId.IsEmpty).ToArray();
        var normal = images.FirstOrDefault(image => image.Size == 0) ?? images.FirstOrDefault();
        if (normal is null) return null;
        var largest = images.OrderByDescending(image => image.HasWidth && image.Width > 0 ? image.Width * 8 : image.Size).First();
        return new("https://i.scdn.co/image/" + Convert.ToHexStringLower(normal.FileId.Span),
            normal.HasWidth ? normal.Width : null, normal.HasHeight ? normal.Height : null,
            LargestUrl: "https://i.scdn.co/image/" + Convert.ToHexStringLower(largest.FileId.Span));
    }
}
