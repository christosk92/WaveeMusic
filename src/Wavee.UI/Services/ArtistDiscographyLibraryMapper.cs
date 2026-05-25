using System;
using System.Collections.Generic;
using Wavee.UI.Contracts;
using Wavee.UI.Models;

namespace Wavee.UI.Services;

/// <summary>
/// Merges an artist discography page with the user's saved albums and liked-song albums.
/// Pure, no I/O; safe to unit-test without WinUI.
/// </summary>
internal static class ArtistDiscographyLibraryMapper
{
    public static IReadOnlyList<LibraryArtistAlbumDto> Map(
        IReadOnlyList<ArtistReleaseResult>? releases,
        IReadOnlyList<LibraryAlbumDto>? savedAlbums,
        IReadOnlyList<LikedSongDto>? likedSongs)
    {
        if (releases is null || releases.Count == 0)
            return Array.Empty<LibraryArtistAlbumDto>();

        var savedIndex = AlbumIndex.Build(
            savedAlbums ?? Array.Empty<LibraryAlbumDto>(),
            static album => album.Id,
            static album => album.Name,
            static album => album.ImageUrl);

        var likedIndex = AlbumIndex.Build(
            likedSongs ?? Array.Empty<LikedSongDto>(),
            static song => song.AlbumId,
            static song => song.AlbumName,
            static song => song.ImageUrl);

        var result = new List<LibraryArtistAlbumDto>(releases.Count);
        foreach (var release in releases)
        {
            var albumUri = ResolveAlbumUri(release);
            var albumBareId = ExtractBareId(albumUri);
            var name = string.IsNullOrWhiteSpace(release.Name) ? "Unknown" : release.Name!;

            var saved = savedIndex.Find(albumBareId, name);
            var liked = likedIndex.Find(albumBareId, name);

            result.Add(new LibraryArtistAlbumDto
            {
                Id = albumUri,
                Name = name,
                ImageUrl = FirstNonWhiteSpace(release.ImageUrl, saved?.ImageUrl, liked?.ImageUrl),
                Year = release.Year,
                AlbumType = release.Type,
                IsSaved = saved is not null,
                ContainsLikedSongs = liked is not null
            });
        }

        return result;
    }

    private static string ResolveAlbumUri(ArtistReleaseResult release)
    {
        if (!string.IsNullOrWhiteSpace(release.Uri))
            return release.Uri!.Trim();

        var id = release.Id?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(id))
            return string.Empty;

        return id.StartsWith("spotify:album:", StringComparison.OrdinalIgnoreCase)
            ? id
            : $"spotify:album:{id}";
    }

    private static string ExtractBareId(string? idOrUri)
    {
        if (string.IsNullOrWhiteSpace(idOrUri))
            return string.Empty;

        var trimmed = idOrUri.Trim();
        var lastColon = trimmed.LastIndexOf(':');
        return lastColon >= 0 ? trimmed[(lastColon + 1)..] : trimmed;
    }

    private static string NormalizeName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim();

    private static string? FirstNonWhiteSpace(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private sealed class AlbumIndex
    {
        private readonly Dictionary<string, AlbumIndexEntry> _byId = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AlbumIndexEntry> _byName = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AlbumIndexEntry> _byNameWhenIdMissing = new(StringComparer.OrdinalIgnoreCase);

        public static AlbumIndex Build<T>(
            IEnumerable<T> source,
            Func<T, string?> idSelector,
            Func<T, string?> nameSelector,
            Func<T, string?> imageSelector)
        {
            var index = new AlbumIndex();
            foreach (var item in source)
            {
                var id = ExtractBareId(idSelector(item));
                var name = NormalizeName(nameSelector(item));
                var entry = new AlbumIndexEntry(FirstNonWhiteSpace(imageSelector(item)));

                if (!string.IsNullOrEmpty(id))
                    AddOrUpgrade(index._byId, id, entry);

                if (!string.IsNullOrEmpty(name))
                {
                    AddOrUpgrade(index._byName, name, entry);
                    if (string.IsNullOrEmpty(id))
                        AddOrUpgrade(index._byNameWhenIdMissing, name, entry);
                }
            }

            return index;
        }

        public AlbumIndexEntry? Find(string releaseBareId, string releaseName)
        {
            if (!string.IsNullOrEmpty(releaseBareId) && _byId.TryGetValue(releaseBareId, out var byId))
                return byId;

            var name = NormalizeName(releaseName);
            if (string.IsNullOrEmpty(name))
                return null;

            if (string.IsNullOrEmpty(releaseBareId))
                return _byName.TryGetValue(name, out var byName) ? byName : null;

            return _byNameWhenIdMissing.TryGetValue(name, out var missingIdName)
                ? missingIdName
                : null;
        }

        private static void AddOrUpgrade(
            Dictionary<string, AlbumIndexEntry> target,
            string key,
            AlbumIndexEntry entry)
        {
            if (!target.TryGetValue(key, out var existing) || string.IsNullOrWhiteSpace(existing.ImageUrl))
                target[key] = entry;
        }
    }

    internal sealed record AlbumIndexEntry(string? ImageUrl);
}
