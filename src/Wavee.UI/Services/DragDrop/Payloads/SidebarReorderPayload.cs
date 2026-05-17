using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wavee.UI.Services.DragDrop.Json;

namespace Wavee.UI.Services.DragDrop.Payloads;

public enum SidebarItemKind
{
    Playlist,
    Folder,
}

/// <summary>
/// A sidebar row being dragged onto another sidebar row to reorder / nest.
/// Folders carry no public URL — <see cref="HttpsUrls"/> is empty so
/// <c>DragPackageWriter</c> skips the external Text/WebLink fallbacks for them.
/// </summary>
public sealed class SidebarReorderPayload : IDragPayload
{
    public string SourceUri { get; }
    public SidebarItemKind ItemKind { get; }
    public string? CurrentParentFolderId { get; }

    public DragPayloadKind Kind => DragPayloadKind.SidebarItem;
    public string InternalFormat => DragFormats.SidebarItem;
    public int ItemCount => 1;

    public IReadOnlyList<string> HttpsUrls => ItemKind == SidebarItemKind.Folder
        || Wavee.UI.Helpers.SpotifyUriHelper.ToHttps(SourceUri) is not { } u
        ? Array.Empty<string>()
        : [u];

    [JsonConstructor]
    public SidebarReorderPayload(string sourceUri, SidebarItemKind itemKind, string? currentParentFolderId = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceUri);
        SourceUri = sourceUri;
        ItemKind = itemKind;
        CurrentParentFolderId = currentParentFolderId;
    }

    public string Serialize() =>
        JsonSerializer.Serialize(new SidebarDto(SourceUri, ItemKind, CurrentParentFolderId), DragPayloadJsonContext.Default.SidebarDto);

    public static SidebarReorderPayload Deserialize(string raw)
    {
        var dto = JsonSerializer.Deserialize(raw, DragPayloadJsonContext.Default.SidebarDto)
                  ?? throw new InvalidOperationException("SidebarReorderPayload deserialization returned null");
        return new SidebarReorderPayload(dto.SourceUri ?? string.Empty, dto.ItemKind, dto.CurrentParentFolderId);
    }

    internal sealed record SidebarDto(string? SourceUri, SidebarItemKind ItemKind, string? CurrentParentFolderId);
}
