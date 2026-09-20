using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Wavee.Core;

namespace Wavee.SpotifyLive;

/// <summary>Defensive, operation-specific projection from captured Pathfinder response shapes to Core. Malformed
/// optional branches are ignored; a malformed required root produces null or an empty collection.</summary>
public static class ConcertPathfinderMapper
{
    public static ArtistConcertSchedule? MapArtistSchedule(JsonElement root)
    {
        if (!TryData(root, out var data) || !TryProperty(data, "artistUnion", out var artistUnion))
            return null;

        string? uri = String(artistUnion, "uri");
        string? name = NestedString(artistUnion, "profile", "name");
        if (!IsKind(uri, EntityKind.Artist) || name is null)
            return null;

        var artist = new ArtistRef(EntityUri.IdOf(uri!), uri!, name);
        var header = TryProperty(artistUnion, "headerImage", out var headerImage)
            ? MapImage(headerImage, preferLastUnmeasured: true)
            : null;

        var nearby = new List<Concert>();
        string? nearbyName = null;
        if (TryProperty(data, "nearby", out var nearbyRoot))
        {
            nearbyName = String(nearbyRoot, "locationName");
            if (TryPath(nearbyRoot, out var items, "concerts", "items"))
                MapConcertItems(items, nearby, isNearUser: true);
        }

        var concerts = new List<Concert>();
        if (TryPath(data, out var concertItems, "concerts", "concerts", "items"))
            MapConcertItems(concertItems, concerts, isNearUser: false);

        return new ArtistConcertSchedule(artist, header, Deduplicate(concerts), Deduplicate(nearby), nearbyName);
    }

    public static IReadOnlyList<ConcertConcept> MapConcepts(JsonElement root)
    {
        if (!TryData(root, out var data) || !TryPath(data, out var items, "concertConcepts", "items") ||
            items.ValueKind != JsonValueKind.Array)
            return Array.Empty<ConcertConcept>();

        var result = new List<ConcertConcept>();
        foreach (var wrapper in items.EnumerateArray())
        {
            if (!TryProperty(wrapper, "data", out var concept))
                continue;
            string? uri = String(concept, "uri");
            string? name = String(concept, "name");
            if (uri is null || name is null)
                continue;
            result.Add(new ConcertConcept(uri, name, Double(wrapper, "weight") ?? 0d));
        }
        return result;
    }

    public static ConcertFeedPage? MapFeed(JsonElement root)
    {
        if (!TryData(root, out var data) || !TryPath(data, out var sectionItems, "liveEventsFeed", "sections") ||
            sectionItems.ValueKind != JsonValueKind.Array)
            return null;

        var sections = new List<ConcertFeedSection>();
        var seenConcerts = new HashSet<string>(StringComparer.Ordinal);
        var seenPromotions = new HashSet<string>(StringComparer.Ordinal);
        string? paginationKey = null;

        foreach (var section in sectionItems.EnumerateArray())
        {
            switch (String(section, "__typename"))
            {
                case "ConcertCarousel":
                    AddFeedSection(sections, section, ConcertFeedSectionKind.Nearby, seenConcerts, seenPromotions);
                    break;
                case "LiveEventSection":
                    AddFeedSection(sections, section, ConcertFeedSectionKind.Recommended, seenConcerts, seenPromotions);
                    break;
                case "AllEvents":
                    paginationKey = String(section, "paginationKey");
                    if (TryProperty(section, "sections", out var nested) && nested.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var nestedSection in nested.EnumerateArray())
                            AddFeedSection(sections, nestedSection, ConcertFeedSectionKind.AllEvents,
                                seenConcerts, seenPromotions);
                    }
                    break;
            }
        }

        return new ConcertFeedPage(sections, paginationKey);
    }

    public static int? MapFeedCount(JsonElement root) =>
        TryData(root, out var data) && TryPath(data, out var count, "concerts", "concerts", "totalCount") &&
        count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out int value)
            ? value
            : null;

    public static ConcertDetails? MapDetails(JsonElement root)
    {
        if (!TryData(root, out var data) || !TryProperty(data, "concert", out var source))
            return null;

        var summary = MapConcert(source, false);
        if (summary is null)
            return null;

        var artists = MapArtists(source);
        var offers = MapOffers(source);
        var related = new List<Concert>();
        if (TryPath(source, out var relatedItems, "relatedConcerts", "items"))
            MapConcertItems(relatedItems, related, false);
        var concepts = MapConceptItems(source);

        JsonElement location = default;
        bool hasLocation = TryProperty(source, "location", out location);
        var coordinates = hasLocation && TryProperty(location, "coordinates", out var coordinateRoot)
            ? MapCoordinates(coordinateRoot)
            : null;
        string? venueUri = TryPath(source, out var venueData, "venue", "data")
            ? String(venueData, "uri")
            : null;
        string? metroName = hasLocation && TryProperty(location, "metroAreaLocation", out var metro)
            ? String(metro, "fullName")
            : null;
        string? metroId = hasLocation && TryProperty(location, "metroAreaLocation", out metro)
            ? String(metro, "geonameId")
            : null;

        return new ConcertDetails(
            summary,
            artists,
            offers,
            Deduplicate(related),
            concepts,
            hasLocation ? String(location, "region") : null,
            hasLocation ? String(location, "country") : null,
            venueUri,
            metroName,
            metroId,
            coordinates,
            Date(source, "doorsOpenTimeIsoString"),
            String(source, "ageRestriction"),
            String(source, "status"));
    }

    public static ConcertPlace? MapUserLocation(JsonElement root) =>
        TryData(root, out var data) && TryPath(data, out var location, "me", "profile", "location")
            ? MapPlace(location)
            : null;

    public static bool? MapIsInferred(JsonElement root)
    {
        if (!TryData(root, out var data) || !TryPath(data, out var location, "me", "profile", "location") ||
            !TryProperty(location, "isInferred", out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return null;
        return value.GetBoolean();
    }

    public static IReadOnlyList<ConcertPlace> MapLocations(JsonElement root)
    {
        if (!TryData(root, out var data) || !TryPath(data, out var items, "concertLocations", "items") ||
            items.ValueKind != JsonValueKind.Array)
            return Array.Empty<ConcertPlace>();
        return MapPlaceItems(items);
    }

    public static ConcertLocationSnapshot? MapLocationSnapshot(JsonElement root)
    {
        if (!TryData(root, out var data))
            return null;
        IReadOnlyList<ConcertPlace> matches = TryPath(data, out var items, "concertLocations", "items") &&
            items.ValueKind == JsonValueKind.Array
                ? MapPlaceItems(items)
                : Array.Empty<ConcertPlace>();
        ConcertPlace? saved = TryPath(data, out var savedElement, "me", "profile", "location")
            ? MapPlace(savedElement)
            : null;
        return new ConcertLocationSnapshot(matches, saved);
    }

    public static bool MapSaveLocation(JsonElement root) =>
        TryData(root, out var data) && TryPath(data, out var value, "storeUserLocation", "success") &&
        value.ValueKind is JsonValueKind.True;

    static void AddFeedSection(List<ConcertFeedSection> destination, JsonElement source,
        ConcertFeedSectionKind kind, HashSet<string> seenConcerts, HashSet<string> seenPromotions)
    {
        string key = String(source, "key") ?? kind.ToString();
        string? description = String(source, "description");
        var concerts = new List<Concert>();
        var promotions = new List<PlaylistRef>();

        if (TryProperty(source, "concerts", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var wrapper in items.EnumerateArray())
            {
                if (!TryProperty(wrapper, "data", out var item))
                    continue;
                string? uri = String(item, "uri");
                if (IsKind(uri, EntityKind.Concert))
                {
                    var concert = MapConcert(item, kind == ConcertFeedSectionKind.Nearby);
                    if (concert is not null && seenConcerts.Add(concert.Uri))
                        concerts.Add(concert);
                }
                else if (IsKind(uri, EntityKind.Playlist))
                {
                    var promotion = MapPlaylist(item);
                    if (promotion is not null && seenPromotions.Add(promotion.Uri))
                        promotions.Add(promotion);
                }
            }
        }

        if (concerts.Count == 0 && promotions.Count == 0)
            return;
        destination.Add(new ConcertFeedSection(key, kind, concerts,
            promotions.Count == 0 ? null : promotions, description));
    }

    static PlaylistRef? MapPlaylist(JsonElement source)
    {
        string? uri = String(source, "uri");
        string? name = String(source, "name");
        if (!IsKind(uri, EntityKind.Playlist) || name is null)
            return null;
        Image? cover = null;
        if (TryPath(source, out var imageItems, "images", "items") && imageItems.ValueKind == JsonValueKind.Array)
        {
            foreach (var imageItem in imageItems.EnumerateArray())
            {
                cover = MapImage(imageItem);
                if (cover is not null)
                    break;
            }
        }
        return new PlaylistRef(uri!, name, cover, String(source, "description") ?? string.Empty);
    }

    static Concert? MapConcert(JsonElement source, bool isNearUser)
    {
        if (TryProperty(source, "data", out var nested))
            source = nested;
        string? uri = String(source, "uri");
        var date = Date(source, "startDateIsoString");
        if (!IsKind(uri, EntityKind.Concert) || date is null)
            return null;

        JsonElement location = default;
        bool hasLocation = TryProperty(source, "location", out location);
        var artists = MapArtists(source);

        // Prefer a concert-level image (with its own extracted dark accent) over the first-artist avatar; a related
        // concert with no concert image still borrows its first artist's banner accent for the tinted no-artwork pane.
        Image? image = null;
        uint? accent = null;
        if (TryFirstImageItem(source, out var imageItem))
        {
            image = MapImage(imageItem);
            accent = ExtractedColor(imageItem);
        }
        image ??= artists.FirstOrDefault(x => x.Image is not null)?.Image;
        accent ??= artists.FirstOrDefault(x => x.AccentColor is not null)?.AccentColor;

        return new Concert(
            uri!,
            String(source, "title"),
            hasLocation ? String(location, "name") ?? string.Empty : string.Empty,
            hasLocation ? String(location, "city") ?? string.Empty : string.Empty,
            date.Value,
            Bool(source, "festival") ?? false,
            isNearUser,
            hasLocation ? String(location, "region") : null,
            hasLocation ? String(location, "country") : null,
            artists,
            image,
            accent);
    }

    static IReadOnlyList<ConcertArtist> MapArtists(JsonElement source)
    {
        if (!TryPath(source, out var items, "artists", "items") || items.ValueKind != JsonValueKind.Array)
            return Array.Empty<ConcertArtist>();
        var artists = new List<ConcertArtist>();
        foreach (var wrapper in items.EnumerateArray())
        {
            var artist = TryProperty(wrapper, "data", out var data) ? data : wrapper;
            string? name = NestedString(artist, "profile", "name");
            if (name is null)
                continue;
            Image? avatar = TryPath(artist, out var avatarNode, "visuals", "avatarImage") ? MapImage(avatarNode) : null;
            // The lineup branch carries a top-level headerImage.data.sources (maxWidth/maxHeight, no colour); the
            // related/feed branch carries visuals.headerImage.sources (width/height) + its extracted dark accent.
            Image? header = null;
            uint? accent = null;
            if (TryProperty(artist, "headerImage", out var headerNode))
                header = MapImage(headerNode, preferLastUnmeasured: true);
            if (header is null && TryPath(artist, out var visualHeader, "visuals", "headerImage"))
            {
                header = MapImage(visualHeader, preferLastUnmeasured: true);
                accent = ExtractedColor(visualHeader);
            }
            artists.Add(new ConcertArtist(name, String(artist, "uri"), avatar, header, accent));
        }
        return artists;
    }

    // The first images.items[] element (concert-level cover) when present — it carries both sources[] and extractedColors.
    static bool TryFirstImageItem(JsonElement source, out JsonElement item)
    {
        item = default;
        if (!TryPath(source, out var items, "images", "items") || items.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var candidate in items.EnumerateArray())
        {
            item = candidate;
            return true;
        }
        return false;
    }

    // An image/visual node's extracted dark tone (extractedColors.colorDark.hex) → opaque ARGB, reusing the app's shared
    // hex parser. A Spotify generic fallback (isFallback) or a malformed/absent node yields null — never a wrong colour.
    static uint? ExtractedColor(JsonElement node)
    {
        if (!TryProperty(node, "extractedColors", out var colors) || !TryProperty(colors, "colorDark", out var dark))
            return null;
        if (Bool(dark, "isFallback") == true)
            return null;
        return SpotifyExportMapper.HexToArgb(String(dark, "hex"));
    }

    static IReadOnlyList<ConcertOffer> MapOffers(JsonElement source)
    {
        if (!TryPath(source, out var items, "offers", "items") || items.ValueKind != JsonValueKind.Array)
            return Array.Empty<ConcertOffer>();
        var offers = new List<ConcertOffer>();
        foreach (var item in items.EnumerateArray())
        {
            string? provider = String(item, "providerName");
            if (provider is null)
                continue;
            string? availability = String(item, "availability");
            DateTimeOffset? starts = null;
            DateTimeOffset? ends = null;
            if (TryProperty(item, "dates", out var dates))
            {
                starts = Date(dates, "startDateIsoString");
                ends = Date(dates, "endDateIsoString");
            }
            offers.Add(new ConcertOffer(
                provider,
                String(item, "url"),
                availability?.ToUpperInvariant() switch
                {
                    "AVAILABLE" => ConcertOfferAvailability.Available,
                    "UNAVAILABLE" => ConcertOfferAvailability.Unavailable,
                    _ => ConcertOfferAvailability.Unknown,
                },
                String(item, "providerImageUrl"),
                Decimal(item, "minPrice"),
                Decimal(item, "maxPrice"),
                String(item, "currency"),
                String(item, "saleType"),
                starts,
                ends,
                Bool(item, "hasPromoCodes") ?? false,
                Bool(item, "firstParty") ?? false));
        }
        return offers;
    }

    static IReadOnlyList<ConcertConcept> MapConceptItems(JsonElement source)
    {
        if (!TryPath(source, out var items, "concepts", "items") || items.ValueKind != JsonValueKind.Array)
            return Array.Empty<ConcertConcept>();
        var concepts = new List<ConcertConcept>();
        foreach (var wrapper in items.EnumerateArray())
        {
            if (!TryProperty(wrapper, "data", out var item))
                continue;
            string? uri = String(item, "uri");
            string? name = String(item, "name");
            if (uri is not null && name is not null)
                concepts.Add(new ConcertConcept(uri, name, Double(wrapper, "weight") ?? 0d));
        }
        return concepts;
    }

    static IReadOnlyList<ConcertPlace> MapPlaceItems(JsonElement items)
    {
        var places = new List<ConcertPlace>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items.EnumerateArray())
        {
            var place = MapPlace(item);
            string? key = place is null ? null :
                !string.IsNullOrWhiteSpace(place.Id) ? "id:" + place.Id : "geohash:" + place.GeoHash;
            if (place is not null && key is not null && seen.Add(key))
                places.Add(place);
        }
        return places;
    }

    static ConcertPlace? MapPlace(JsonElement source)
    {
        string? id = String(source, "geonameId");
        string? geoHash = String(source, "geoHash");
        string? name = String(source, "name");
        if ((id is null && geoHash is null) || name is null)
            return null;
        GeoCoordinates? coordinates = TryProperty(source, "coordinates", out var coordinateRoot)
            ? MapCoordinates(coordinateRoot)
            : null;
        return new ConcertPlace(id ?? string.Empty, name, String(source, "region"), String(source, "country"),
            geoHash, coordinates);
    }

    static GeoCoordinates? MapCoordinates(JsonElement source)
    {
        double? latitude = Double(source, "latitude");
        double? longitude = Double(source, "longitude");
        return latitude is { } lat && longitude is { } lon ? new GeoCoordinates(lat, lon) : null;
    }

    static void MapConcertItems(JsonElement items, List<Concert> destination, bool isNearUser)
    {
        if (items.ValueKind != JsonValueKind.Array)
            return;
        foreach (var item in items.EnumerateArray())
        {
            var concert = MapConcert(item, isNearUser);
            if (concert is not null)
                destination.Add(concert);
        }
    }

    static IReadOnlyList<Concert> Deduplicate(IEnumerable<Concert> source)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return source.Where(x => seen.Add(x.Uri)).ToArray();
    }

    static Image? MapImage(JsonElement source, bool preferLastUnmeasured = false)
    {
        if (TryProperty(source, "data", out var data))
            source = data;
        if (!TryProperty(source, "sources", out var sources) || sources.ValueKind != JsonValueKind.Array)
            return null;

        // Spotify orders a few image branches from the smallest rendition to the largest (the artist-concert header
        // capture starts at 16px and ends at the full banner), while other branches include explicit dimensions in no
        // guaranteed order. Picking the first valid URL made a perfectly valid hero look like a flat placeholder.
        // Prefer the largest measured rendition. Unmeasured avatar arrays are largest-first, while unmeasured wide
        // header arrays are smallest-first, so header call sites explicitly opt into keeping the last rendition.
        Image? best = null;
        long bestArea = -1;
        foreach (var candidate in sources.EnumerateArray())
        {
            string? url = String(candidate, "url");
            if (url is null)
                continue;
            // ImageV2 sources (lineup headerImage.data) key their dimensions maxWidth/maxHeight; the older shapes use width/height.
            int? width = Int(candidate, "width") ?? Int(candidate, "maxWidth");
            int? height = Int(candidate, "height") ?? Int(candidate, "maxHeight");
            long area = width is > 0 && height is > 0 ? (long)width.Value * height.Value : 0;
            if (best is null || area > bestArea || (preferLastUnmeasured && area == bestArea && area == 0))
            {
                best = new Image(url, width, height);
                bestArea = area;
            }
        }
        return best;
    }

    static bool TryData(JsonElement root, out JsonElement data) => TryProperty(root, "data", out data);

    static bool TryPath(JsonElement root, out JsonElement value, params string[] path)
    {
        value = root;
        foreach (string part in path)
        {
            if (!TryProperty(value, part, out value))
                return false;
        }
        return true;
    }

    static bool TryProperty(JsonElement source, string name, out JsonElement value)
    {
        if (source.ValueKind == JsonValueKind.Object && source.TryGetProperty(name, out value))
            return true;
        value = default;
        return false;
    }

    static string? NestedString(JsonElement source, string objectName, string propertyName) =>
        TryProperty(source, objectName, out var nested) ? String(nested, propertyName) : null;

    static string? String(JsonElement source, string name)
    {
        if (!TryProperty(source, name, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        string? text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    static bool? Bool(JsonElement source, string name)
    {
        if (!TryProperty(source, name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    static int? Int(JsonElement source, string name) =>
        TryProperty(source, name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out int result) ? result : null;

    static double? Double(JsonElement source, string name) =>
        TryProperty(source, name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out double result) ? result : null;

    static decimal? Decimal(JsonElement source, string name) =>
        TryProperty(source, name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetDecimal(out decimal result) ? result : null;

    static DateTimeOffset? Date(JsonElement source, string name)
    {
        string? value = String(source, name);
        return value is not null && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var result)
            ? result
            : null;
    }

    // The uri must BE that kind and carry a non-empty id (the old prefix test's `Length > prefix.Length` half).
    static bool IsKind(string? value, EntityKind kind) =>
        value is not null && EntityUri.Parse(value) is { Id.Length: > 0 } e && e.Kind == kind;
}
