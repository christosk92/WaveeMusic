namespace Wavee.Tests.Modules.Fixtures;

/// <summary>
/// Station playlists and stream bodies shaped exactly like what Icecast/SHOUTcast front-ends serve. Kept as raw
/// string literals so the bodies are verbatim but need no copy-to-output plumbing in the test csproj.
/// </summary>
public static class RadioFixtures
{
    /// <summary>A SHOUTcast <c>.pls</c> with the entries deliberately out of order.</summary>
    public const string Pls = """
    [playlist]
    numberofentries=2
    File2=http://ice2.example.org:8000/stream2.mp3
    Title2=Example Radio (backup)
    Length2=-1
    File1=http://ice1.example.org:8000/stream1.mp3
    Title1=Example Radio
    Length1=-1
    Version=2
    """;

    /// <summary>An extended <c>.m3u</c> station file.</summary>
    public const string M3u = """
    #EXTM3U
    #EXTINF:-1,Example Radio - 128 kbit/s MP3
    http://ice1.example.org:8000/stream1.mp3
    #EXTINF:-1,Example Radio - 64 kbit/s AAC
    http://ice1.example.org:8000/stream1.aac
    """;

    /// <summary>A bare <c>.m3u</c> with no header at all — very common for station links.</summary>
    public const string M3uBare = """
    http://ice1.example.org:8000/stream1.mp3
    """;

    /// <summary>An <c>.m3u</c> with only relative entries, to exercise base-url resolution.</summary>
    public const string M3uRelative = """
    #EXTM3U
    stream1.mp3
    """;

    /// <summary>An HLS manifest served under an <c>.m3u8</c> name: NOT a station playlist to unwrap.</summary>
    public const string Hls = """
    #EXTM3U
    #EXT-X-VERSION:3
    #EXT-X-STREAM-INF:BANDWIDTH=128000,CODECS="mp4a.40.2"
    chunklist.m3u8
    """;

    /// <summary>A <c>.pls</c> with no <c>FileN=</c> entries at all.</summary>
    public const string PlsEmpty = """
    [playlist]
    numberofentries=0
    Version=2
    """;
}
