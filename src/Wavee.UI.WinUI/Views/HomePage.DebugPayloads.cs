namespace Wavee.UI.WinUI.Views;

/// <summary>
/// Concrete payload records that back the HomePage debug flyout ("Customize this
/// section" → Debug → "View viewmodel JSON" / "View raw Spotify"). The pre-AOT
/// implementation used anonymous types serialized through a reflection-based
/// <see cref="System.Text.Json.JsonSerializer"/> overload. JsonSerializerContext
/// source generators cannot reach anonymous types, so each shape is promoted
/// to an explicit record and registered in
/// <see cref="Wavee.UI.WinUI.Json.WaveeUiWinUiJsonContext"/>.
/// </summary>
public sealed record HomeDebugMissingSectionPayload(
    string Message,
    string? Title,
    string? SectionUri,
    string SectionType,
    int ItemCount);

public sealed record HomeDebugSectionViewModel(
    string? Title,
    string? Subtitle,
    string SectionType,
    string? SectionUri,
    string? HeaderEntityName,
    string? HeaderEntityImageUrl,
    string? HeaderEntityUri,
    int ItemCount,
    HomeDebugSectionItem[] Items);

public sealed record HomeDebugSectionItem(
    int Index,
    string? Uri,
    string? Title,
    string? Subtitle,
    string? ImageUrl,
    string ContentType,
    string? ColorHex,
    string? PlaceholderGlyph,
    bool IsBaselineLoading,
    bool HasBaselinePreview,
    string? HeroImageUrl,
    string? HeroColorHex,
    string? CanvasUrl,
    string? CanvasThumbnailUrl,
    string? AudioPreviewUrl,
    string? BaselineGroupTitle,
    HomeDebugSectionPreviewTrack[] PreviewTracks);

public sealed record HomeDebugSectionPreviewTrack(
    string? Uri,
    string? Name,
    string? CoverArtUrl,
    string? ColorHex,
    string? CanvasUrl,
    string? CanvasThumbnailUrl,
    string? AudioPreviewUrl);
