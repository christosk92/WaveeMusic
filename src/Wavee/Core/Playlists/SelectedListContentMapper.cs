using Google.Protobuf;
using Wavee.Protocol.Playlist;

namespace Wavee.Core.Playlists;

public static class SelectedListContentMapper
{
    public static RootlistSnapshot MapRootlist(SelectedListContent content, DateTimeOffset fetchedAt)
    {
        ArgumentNullException.ThrowIfNull(content);

        var items = new List<RootlistEntry>();
        var decorations = new Dictionary<string, RootlistDecoration>(StringComparer.Ordinal);
        var rawItems = content.Contents?.Items ?? [];
        var metaItems = content.Contents?.MetaItems ?? [];

        for (int i = 0; i < rawItems.Count; i++)
        {
            var item = rawItems[i];
            if (TryParseFolderStart(item.Uri, out var folderId, out var folderName))
            {
                items.Add(new RootlistFolderStart(folderId!, folderName!));
                continue;
            }

            if (TryParseFolderEnd(item.Uri, out folderId))
            {
                items.Add(new RootlistFolderEnd(folderId!));
                continue;
            }

            if (!item.Uri.StartsWith("spotify:playlist:", StringComparison.Ordinal))
                continue;

            items.Add(new RootlistPlaylist(item.Uri));

            if (i >= metaItems.Count)
                continue;

            var meta = metaItems[i];
            decorations[item.Uri] = new RootlistDecoration
            {
                Revision = meta.Revision?.ToByteArray() ?? [],
                // Store null (not "") when the rootlist payload has no usable
                // name — downstream fallbacks in LibraryDataService use `??`
                // which only catches null. Empty strings would otherwise
                // propagate to the sidebar as a blank title row.
                Name = string.IsNullOrWhiteSpace(meta.Attributes?.Name) ? null : meta.Attributes.Name,
                Description = meta.Attributes?.Description,
                ImageUrl = PickImageUrl(meta.Attributes),
                OwnerUsername = meta.OwnerUsername,
                Length = meta.Length,
                IsPublic = item.Attributes?.Public ?? false,
                IsCollaborative = meta.Attributes?.Collaborative ?? false,
                StatusCode = InferRootlistStatusCode(meta)
            };
        }

        return new RootlistSnapshot
        {
            Revision = content.Revision?.ToByteArray() ?? [],
            Items = items,
            Decorations = decorations,
            FetchedAt = fetchedAt
        };
    }

    public static CachedPlaylist MapPlaylist(
        string playlistUri,
        SelectedListContent content,
        string? currentUsername,
        DateTimeOffset fetchedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistUri);
        ArgumentNullException.ThrowIfNull(content);

        var items = (content.Contents?.Items ?? [])
            .Select(MapPlaylistItem)
            .ToArray();

        return new CachedPlaylist
        {
            Uri = playlistUri,
            Revision = content.Revision?.ToByteArray() ?? [],
            Name = content.Attributes?.Name ?? "Unknown",
            Description = content.Attributes?.Description,
            ImageUrl = PickImageUrl(content.Attributes),
            HeaderImageUrl = PickFormatAttribute(content.Attributes, "header_image_url_desktop"),
            OwnerUsername = content.OwnerUsername ?? "",
            Length = content.Length,
            IsCollaborative = content.Attributes?.Collaborative ?? false,
            DeletedByOwner = content.Attributes?.DeletedByOwner ?? false,
            AbuseReportingEnabled = content.AbuseReportingEnabled,
            BasePermission = MapBasePermission(content.OwnerUsername, currentUsername, content.Capabilities),
            Capabilities = MapCapabilities(content.Capabilities, content.AbuseReportingEnabled),
            Items = items,
            // The proto's top-level `Format` field (field 11) lives separately from
            // `FormatAttributes` (field 12) — but downstream code (chart-mode detection,
            // editorial chrome) finds it cleanest to read both as one dict. Inject the
            // sibling `format` value as a synthetic key here so consumers stay simple.
            FormatAttributes = ExtractFormatAttributes(
                content.Attributes?.FormatAttributes,
                content.Attributes?.Format),
            AvailableSignals = ExtractAvailableSignals(content.Contents),
            HasContentsSnapshot = content.Contents != null,
            FetchedAt = fetchedAt
        };
    }

    /// <summary>
    /// Extract the list of server-advertised signal identifiers from a
    /// SelectedListContent's contents. Each entry is a ready-to-POST string
    /// (e.g. <c>session_control_display$&lt;group&gt;$&lt;option&gt;</c>) — the caller
    /// should not attempt to reconstruct it. Empty for playlists without
    /// session-control chrome, or when the server omits the field.
    /// </summary>
    /// <remarks>
    /// First tries the known field (via <see cref="ListItems.AvailableSignals"/>);
    /// if empty, falls back to a tag-walk of the raw serialized bytes so we
    /// can pick up the signals even if the proto field number we guessed
    /// doesn't match what the server actually uses. This is a one-time
    /// diagnostic fallback that should be cleaned up once the correct
    /// field number is known.
    /// </remarks>
    public static IReadOnlyList<string> ExtractAvailableSignals(ListItems? contents)
    {
        if (contents is null)
        {
            System.Diagnostics.Debug.WriteLine("[session-signals] Contents is null");
            return Array.Empty<string>();
        }

        if (contents.AvailableSignals is { Count: > 0 } known)
        {
            var result = new List<string>(known.Count);
            foreach (var signal in known)
                if (!string.IsNullOrEmpty(signal.Identifier))
                    result.Add(signal.Identifier);
            System.Diagnostics.Debug.WriteLine($"[session-signals] Known-field path matched {result.Count} signals: {string.Join(", ", result)}");
            return result;
        }

        var probed = ProbeAvailableSignalsFromRawBytes(contents);
        System.Diagnostics.Debug.WriteLine($"[session-signals] Byte-probe found {probed.Count} signals: {string.Join(", ", probed)}");
        return probed;
    }

    /// <summary>
    /// Fallback: re-serialize <paramref name="contents"/> (which preserves
    /// unknown fields verbatim) and walk every top-level field tag. For each
    /// length-delimited field, recursively scan for any nested length-
    /// delimited field whose bytes parse as a UTF-8 string starting with
    /// <c>session_control_display$</c> or equal to <c>session-control-reset</c>.
    /// Returns the collected identifiers.
    /// </summary>
    private static IReadOnlyList<string> ProbeAvailableSignalsFromRawBytes(ListItems contents)
    {
        byte[] bytes;
        try
        {
            bytes = contents.ToByteArray();
        }
        catch
        {
            return Array.Empty<string>();
        }

        var found = new List<string>();
        CollectSignalStrings(bytes, found, depth: 0);
        return found;
    }

    private static void CollectSignalStrings(ReadOnlySpan<byte> bytes, List<string> collector, int depth)
    {
        if (depth > 4 || bytes.IsEmpty) return;

        int i = 0;
        while (i < bytes.Length)
        {
            if (!TryReadVarint(bytes, ref i, out var tag)) return;
            var wireType = (int)(tag & 7);
            switch (wireType)
            {
                case 0: // varint
                    if (!TryReadVarint(bytes, ref i, out _)) return;
                    break;
                case 1: // 64-bit
                    if (i + 8 > bytes.Length) return;
                    i += 8;
                    break;
                case 2: // length-delimited
                    if (!TryReadVarint(bytes, ref i, out var len)) return;
                    if ((int)len < 0 || i + (int)len > bytes.Length) return;
                    var slice = bytes.Slice(i, (int)len);
                    TryMatchSignalString(slice, collector);
                    CollectSignalStrings(slice, collector, depth + 1);
                    i += (int)len;
                    break;
                case 5: // 32-bit
                    if (i + 4 > bytes.Length) return;
                    i += 4;
                    break;
                default:
                    return; // groups / unknown — bail
            }
        }
    }

    private static void TryMatchSignalString(ReadOnlySpan<byte> slice, List<string> collector)
    {
        // Heuristic: a "session_control_display$..." or "session-control-reset"
        // string will be at minimum ~20 ASCII-printable bytes.
        if (slice.Length < 20 || slice.Length > 512) return;
        for (int k = 0; k < slice.Length; k++)
        {
            var b = slice[k];
            if (b < 0x20 || b > 0x7E) return; // non-printable → not our string
        }
        var s = System.Text.Encoding.ASCII.GetString(slice);
        if (s.StartsWith("session_control_display$", StringComparison.Ordinal) ||
            string.Equals(s, "session-control-reset", StringComparison.Ordinal))
        {
            if (!collector.Contains(s))
                collector.Add(s);
        }
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> bytes, ref int offset, out ulong value)
    {
        value = 0;
        int shift = 0;
        while (offset < bytes.Length)
        {
            var b = bytes[offset++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
            if (shift > 63) return false;
        }
        return false;
    }

    /// <summary>
    /// Look up a single key in <see cref="ListAttributes.FormatAttributes"/>. Spotify
    /// uses this for editorial playlist chrome — e.g. <c>header_image_url_desktop</c>
    /// carries a wide hero image URL. Returns null when the key is absent or empty.
    /// </summary>
    public static string? PickFormatAttribute(ListAttributes? attributes, string key)
    {
        if (attributes?.FormatAttributes.Count is not > 0) return null;
        foreach (var attr in attributes.FormatAttributes)
        {
            if (string.Equals(attr.Key, key, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(attr.Value))
                return attr.Value;
        }
        return null;
    }

    // internal so PlaylistDiffApplier (same assembly) can reuse the protobuf
    // Item → CachedPlaylistItem conversion when applying diff ADD ops.
    internal static CachedPlaylistItem MapPlaylistItem(Item item)
    {
        var timestamp = item.Attributes?.Timestamp ?? 0;
        return new CachedPlaylistItem
        {
            Uri = item.Uri,
            AddedBy = item.Attributes?.AddedBy,
            AddedAt = timestamp > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp) : null,
            ItemId = item.Attributes?.ItemId?.ToByteArray(),
            FormatAttributes = ExtractFormatAttributes(item.Attributes?.FormatAttributes)
        };
    }

    /// <summary>
    /// Flatten a repeated-message FormatAttributes list into a plain dictionary.
    /// Spotify sometimes ships duplicate keys; last-write-wins matches the way
    /// the web player treats the list.
    /// </summary>
    // internal so PlaylistDiffApplier (same assembly) can reuse this when merging
    // ItemAttributesPartialState / ListAttributesPartialState payloads.
    internal static IReadOnlyDictionary<string, string> ExtractFormatAttributes(
        Google.Protobuf.Collections.RepeatedField<FormatListAttribute>? attrs,
        string? format = null)
    {
        var hasFormat = !string.IsNullOrEmpty(format);
        var attrCount = attrs?.Count ?? 0;
        if (attrCount == 0 && !hasFormat)
            return _emptyAttributes;
        var dict = new Dictionary<string, string>(
            attrCount + (hasFormat ? 1 : 0),
            StringComparer.Ordinal);
        if (attrs is not null)
        {
            foreach (var attr in attrs)
            {
                if (string.IsNullOrEmpty(attr.Key)) continue;
                dict[attr.Key] = attr.Value ?? string.Empty;
            }
        }
        if (hasFormat) dict["format"] = format!;
        return dict;
    }

    private static readonly IReadOnlyDictionary<string, string> _emptyAttributes
        = new Dictionary<string, string>(0);

    /// <summary>
    /// Key prefix for session-control chip labels in a playlist's format attributes.
    /// Each entry <c>session_control_display.displayName.&lt;option&gt; = &lt;Label&gt;</c>
    /// becomes one chip in the UI row.
    /// </summary>
    public const string SessionControlDisplayNamePrefix = "session_control_display.displayName.";

    /// <summary>
    /// Candidate FormatAttributes keys that carry the session-control group id
    /// (the base62 segment joined into the signal key between the
    /// <c>session_control_display</c> literal and the chosen option). Ordered by
    /// likelihood. The first entry with a non-empty value wins.
    /// </summary>
    /// <remarks>
    /// The exact key was not captured in the original reverse-engineering paste;
    /// leaving a priority list avoids a second packet capture before shipping.
    /// If none of these match, the chip row still renders, but click-to-signal
    /// is disabled — the consumer returns null from
    /// <see cref="ExtractSessionControlGroupId"/> in that case.
    /// </remarks>
    private static readonly string[] SessionControlGroupIdKeys =
    [
        "session_control_display.id",
        "session_control_display.sessionId",
        "session_control_display.groupId",
        "session_control_display.sessionControlId",
        "session_control_display",
    ];

    /// <summary>
    /// Extract ordered chip options from a playlist's format attributes.
    /// Preserves insertion order so the chip row matches Spotify's UI.
    /// Returns an empty list when no session-control-display entries are present.
    /// </summary>
    public static IReadOnlyList<(string OptionKey, string DisplayName)> ExtractSessionControlOptions(
        IReadOnlyDictionary<string, string>? attrs)
    {
        if (attrs is null || attrs.Count == 0)
            return Array.Empty<(string, string)>();

        var results = new List<(string OptionKey, string DisplayName)>();
        foreach (var kvp in attrs)
        {
            if (kvp.Key is null) continue;
            if (!kvp.Key.StartsWith(SessionControlDisplayNamePrefix, StringComparison.Ordinal))
                continue;
            var optionKey = kvp.Key[SessionControlDisplayNamePrefix.Length..];
            if (string.IsNullOrEmpty(optionKey) || string.IsNullOrEmpty(kvp.Value))
                continue;
            results.Add((optionKey, kvp.Value));
        }
        return results;
    }

    /// <summary>
    /// Resolve the session-control group id used in the signal key. Returns null
    /// when no candidate key is present (chips can still render; click dispatch
    /// should be disabled by the consumer).
    /// </summary>
    public static string? ExtractSessionControlGroupId(IReadOnlyDictionary<string, string>? attrs)
    {
        if (attrs is null || attrs.Count == 0)
            return null;
        foreach (var key in SessionControlGroupIdKeys)
        {
            if (attrs.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
        }
        return null;
    }

    public static CachedPlaylistBasePermission MapBasePermission(
        string? ownerUsername,
        string? currentUsername,
        Capabilities? capabilities)
    {
        if (!string.IsNullOrWhiteSpace(ownerUsername) &&
            !string.IsNullOrWhiteSpace(currentUsername) &&
            string.Equals(ownerUsername, currentUsername, StringComparison.OrdinalIgnoreCase))
        {
            return CachedPlaylistBasePermission.Owner;
        }

        return capabilities?.CanEditItems == true
            ? CachedPlaylistBasePermission.Contributor
            : CachedPlaylistBasePermission.Viewer;
    }

    public static CachedPlaylistCapabilities MapCapabilities(
        Capabilities? capabilities,
        bool abuseReportingEnabled)
    {
        if (capabilities == null)
        {
            System.Diagnostics.Debug.WriteLine("[caps] proto MapCapabilities: capabilities=NULL → ViewOnly");
            return CachedPlaylistCapabilities.ViewOnly with
            {
                CanAbuseReport = abuseReportingEnabled
            };
        }

        System.Diagnostics.Debug.WriteLine(
            $"[caps] proto MapCapabilities: hasField? CanEditMetadata={capabilities.HasCanEditMetadata}={capabilities.CanEditMetadata} | View={capabilities.CanView} EditItems={capabilities.CanEditItems} Admin={capabilities.CanAdministratePermissions} Cancel={capabilities.CanCancelMembership}");

        return new CachedPlaylistCapabilities
        {
            CanView = capabilities.CanView,
            CanEditItems = capabilities.CanEditItems,
            CanEditMetadata = capabilities.CanEditMetadata,
            CanAdministratePermissions = capabilities.CanAdministratePermissions,
            CanCancelMembership = capabilities.CanCancelMembership,
            CanAbuseReport = abuseReportingEnabled
        };
    }

    public static string? PickImageUrl(ListAttributes? attributes)
    {
        if (attributes is null) return null;

        // Preferred: pre-rendered URL from PictureSize (size-targeted CDN
        // entries the server provides for editorial / featured playlists).
        if (attributes.PictureSize.Count > 0)
        {
            var fromSized = attributes.PictureSize.FirstOrDefault(static p => p.TargetName == "default")?.Url
                            ?? attributes.PictureSize.FirstOrDefault()?.Url;
            if (!string.IsNullOrEmpty(fromSized)) return fromSized;
        }

        // Fallback: user-uploaded covers ship as a raw `picture` ByteString
        // (the file id) with no PictureSize array. Hex-encode → standard
        // `spotify:image:{id}` URI, which the UI image converter then maps
        // to https://i.scdn.co/image/{id}. Without this fallback, every
        // user-customised playlist with no PictureSize collapses to the
        // mosaic placeholder.
        if (attributes.HasPicture && attributes.Picture.Length > 0)
        {
            var hex = Convert.ToHexString(attributes.Picture.ToByteArray()).ToLowerInvariant();
            return $"spotify:image:{hex}";
        }

        return null;
    }

    private static bool TryParseFolderStart(string uri, out string? folderId, out string? folderName)
    {
        folderId = null;
        folderName = null;

        if (!uri.StartsWith("spotify:start-group:", StringComparison.Ordinal))
            return false;

        // "spotify:start-group:{id}:{name}" is the canonical form, but "spotify:start-group:{id}"
        // (no name segment) is also legal. Previously we silently dropped the 3-segment form,
        // which left the matching end-group to pop a different folder off the stack and cascaded
        // into wrong subfolder nesting. Accept both shapes.
        var parts = uri.Split(':', 4);
        if (parts.Length < 3 || string.IsNullOrEmpty(parts[2]))
            return false;

        folderId = parts[2];
        folderName = parts.Length >= 4 ? DecodeFolderName(parts[3]) : "";
        return true;
    }

    private static bool TryParseFolderEnd(string uri, out string? folderId)
    {
        folderId = null;
        if (!uri.StartsWith("spotify:end-group:", StringComparison.Ordinal))
            return false;

        var parts = uri.Split(':', 3);
        if (parts.Length < 3)
            return false;

        folderId = parts[2];
        return true;
    }

    private static string DecodeFolderName(string rawName)
    {
        if (string.IsNullOrEmpty(rawName))
            return "Folder";

        return Uri.UnescapeDataString(rawName.Replace("+", " ", StringComparison.Ordinal));
    }

    private static int InferRootlistStatusCode(MetaItem meta)
    {
        if (meta.Attributes != null || meta.HasRevision || meta.HasLength || meta.HasOwnerUsername)
            return 200;

        return 404;
    }
}
