namespace Wavee.Local;

/// <summary>
/// Entity-kind discriminator written to the shared <c>entities.entity_type</c>
/// column. Mirrors the integer values used by
/// <c>Wavee.Core.Storage.EntityType</c> so a row written from Wavee.Local
/// reads correctly from Wavee.dll consumers (and vice versa) without either
/// project depending on the other.
///
/// <para>Two enums, same shape, zero data interop issue — see
/// docs/superpowers/specs/2026-05-12-local-files-redesign-design.md
/// finding #2.</para>
/// </summary>
internal enum LocalEntityKind
{
    Unknown = 0,
    Track = 1,
    Album = 2,
    Artist = 3,
    Episode = 4,
    Show = 5,
    Playlist = 6,
    User = 7,
}

/// <summary>
/// Source-of-record marker written to the shared <c>entities.source_type</c>
/// column. Mirrors <c>Wavee.Core.Storage.Abstractions.SourceType</c> integers.
/// </summary>
internal enum LocalEntitySource
{
    Spotify = 0,
    LocalFile = 1,
    HttpStream = 2,
    Podcast = 3,
}
