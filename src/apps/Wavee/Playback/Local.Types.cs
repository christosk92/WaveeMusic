// src/apps/Wavee/Playback/Local.Types.cs — shared by P1a and P1b; edit only through the orchestrator.
namespace Wavee;

public enum LocalState : byte { Present, Missing, CloudOnly }

public enum LocalKind : byte { Audio, Video }

/// <summary>Where a file's front picture sits; never its bytes (§3.6a). Length 0 = none.</summary>
public readonly record struct LocalCover(long Offset, int Length, LocalCoverMime Mime, LocalCoverEncoding Encoding, byte PictureType)
{
    public static LocalCover None => default;
    public bool Exists => Length > 0;
}

public enum LocalCoverMime : byte { Unknown, Jpeg, Png, Webp, Gif, Bmp }

/// <summary>Raw: the bytes are the image. Unsync: ID3 unsynchronised (FF 00 -> FF). Base64: Ogg METADATA_BLOCK_PICTURE,
/// whose decoded bytes are a FLAC PICTURE block (the reader records the offset/length of the base64 text).</summary>
public enum LocalCoverEncoding : byte { Raw, Unsync, Base64 }

/// <summary>What one header read of one file yields (§3.5). Strings are never null ("" when absent).</summary>
public readonly record struct LocalTagSet(
    LocalKind Kind,
    string Title,                 // the title tag, or "" (the caller applies the file-name rule: LocalTags.TitleOrFileName)
    string[] Artists,             // every artist value, in tag order; empty array when none
    string Album,
    string AlbumArtist,
    int TrackNumber, int DiscNumber, int Year,
    int DurationMs, bool DurationEstimated,
    int BitrateKbps,
    LocalCover Cover,
    int VideoWidth, int VideoHeight, bool HasAudio,  // Kind == Video only (D19); 0/0/true for audio
    int SampleRate, int BitsPerSample, int Channels,  // the audio stream's; 0 = unknown (BitsPerSample 0 for lossy formats)
    string Isrc = "")            // the recording's ISRC (ID3 TSRC, Vorbis ISRC), upper case, or "" (#240 "Your files and Spotify": the Exact rung)
{
    public string FirstArtist => Artists.Length > 0 ? Artists[0] : "";
}

/// <summary>One indexed file. Immutable; replaced whole when it changes (§3.3).</summary>
public sealed record LocalEntry(
    long LocalId,                                   // stable, assigned at first sight, never reused (D13)
    int RootId, string RelativePath,
    uint VolumeSerial, UInt128 FileId,              // move/rename identity on NTFS/ReFS
    long Size, long WriteTicks, long ChangeTicks,   // exact compare; ChangeTicks 0 where the volume does not report it
    uint Attributes,                                // FILE_ATTRIBUTE_* incl. the cloud bits
    Spotify.Audio.Format Format,
    LocalTagSet Tags,
    string MatchTitle,                              // Tags.Title, or the file name without its extension
    LocalState State, long LastSeenTicks,
    bool Playable, string Why,                      // Playable false: no decoder in this build (D7); Why says so
    long FirstSeenTicks);                           // when this LocalId was first indexed (UTC ticks); kept across moves and edits

/// <summary>One listed folder (§3.3).</summary>
public sealed record LocalFolder(
    int RootId, string RelativePath, UInt128 FileId,
    ulong Fingerprint,       // xxHash3-64 over the sorted (name, size, write, change) of its audio + image entries
    string[] Images,         // image file names seen in the listing, no bytes read
    bool Complete);          // false: a file failed to read; retried next scan
