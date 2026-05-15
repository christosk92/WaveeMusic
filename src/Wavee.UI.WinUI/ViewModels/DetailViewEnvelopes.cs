using System;
using System.Collections.Generic;
using Wavee.UI.WinUI.Controls.AvatarStack;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;

namespace Wavee.UI.WinUI.ViewModels;

public sealed record ArtistView(
    string? Name,
    string? ArtistImageUrl,
    string? HeaderImageUrl,
    string? HeaderHeroColorHex,
    ArtistPalette? Palette,
    string? MonthlyListeners,
    int? WorldRank,
    long Followers,
    string? Biography,
    bool IsVerified,
    bool IsRegistered,
    ArtistLatestReleaseResult? LatestRelease,
    int AlbumsTotalCount,
    int SinglesTotalCount,
    int CompilationsTotalCount,
    ArtistPinnedItemResult? PinnedItem,
    ArtistWatchFeedResult? WatchFeed);

public sealed record PlaylistView(
    string Id,
    string Name,
    string? Description,
    string? ImageUrl,
    string? HeaderImageUrl,
    string OwnerName,
    string? OwnerId,
    string? OwnerAvatarUrl,
    IReadOnlyDictionary<string, string>? FormatAttributes,
    byte[]? Revision,
    string? SessionControlGroupId,
    bool IsOwner,
    bool IsPublic,
    bool IsCollaborative,
    PlaylistBasePermission BasePermission,
    bool CanEditItems,
    bool CanAdministratePermissions,
    bool CanCancelMembership,
    bool CanAbuseReport,
    bool CanEditMetadata,
    bool CanEditName,
    bool CanEditDescription,
    bool CanEditPicture,
    bool CanEditCollaborative,
    bool CanDelete,
    AlbumPalette? Palette);

public sealed record AlbumView(
    string Id,
    string Name,
    string? ImageUrl,
    string? ColorHex,
    string ArtistId,
    string ArtistName,
    string? ArtistImageUrl,
    IReadOnlyList<AlbumArtistResult> Artists,
    IReadOnlyList<AlbumArtistResult> AllDistinctArtists,
    IReadOnlyList<AvatarStackItem> ArtistAvatarItems,
    IReadOnlyList<HeaderArtistLink> HeaderArtistLinks,
    int OverflowArtistCount,
    int Year,
    string? Type,
    string? Label,
    string? ReleaseDateFormatted,
    string? CopyrightsText,
    bool IsPreRelease,
    DateTimeOffset? PreReleaseEndDateTime,
    string? PreReleaseFormatted,
    string? PreReleaseRelative,
    string? ShareUrl,
    string? MetaInlineLine,
    AlbumPalette? Palette,
    // Cached track count. Seeded by nav prefill (so the skeleton renders the
    // right row count before the detail fetch lands) and overwritten by
    // ApplyDetailAsync once tracks resolve.
    int TotalTracks = 0);
