// ── Playback/LocalLink.Types.cs ─────────────────────────────────────────────────────────────────────────────────────
// the words of "Your files and Spotify": a Spotify album or track as the catalogue answered it, a candidate with its evidence, one group
// of files (an album, or a song with no album) and the view the page reads (#240 phase 1, the read side)
//
// Role: PURE (immutable records; built on the matcher's thread, read on the UI thread)
// Spec: C:\WAVEE\fgpu\handoff\request-plans\240-spotify-linking-concept.md §4 (US1, US2, US4, US5), §6 (evidence, not scores), §7

namespace Wavee;

/// <summary>How sure a match is, in the words the user reads (concept §4 US1): the first rung of the ladder that answers.</summary>
public enum LinkConfidence : byte
{
    /// <summary>The same ISRC on every song: the only level Wavee may accept for the user, and only after the opt-in.</summary>
    Exact,
    /// <summary>Every track length lines up with the Spotify album (or most ISRCs agree).</summary>
    Strong,
    /// <summary>Title, artist and length.</summary>
    Likely,
    /// <summary>Title only, or several close candidates: the user picks.</summary>
    Check,
}

/// <summary>Why a candidate was proposed: the plain words the review shows (concept §6.2).</summary>
public enum LinkEvidence : byte
{
    /// <summary>"Same ISRC on 11 of 11 songs".</summary>
    SameIsrc,
    /// <summary>"10 of 10 track lengths line up with the Spotify album".</summary>
    LengthsLineUp,
    /// <summary>"Title, artist and length".</summary>
    TitleArtistLength,
    /// <summary>"Title and artist · length differs by 19 s".</summary>
    TitleArtist,
    /// <summary>"No ISRC, title only".</summary>
    TitleOnly,
    /// <summary>"Lengths differ: 4:12 vs 3:39": the song's length is off by more than the slack (another edit, a video's audio). Never above Check,
    /// whatever else agrees (an ISRC included), so it is never accepted for the user.</summary>
    LengthsDiffer,
}

/// <summary>What the user decided about a group. Sticky across rescans and restarts (persisted by <see cref="LocalLink"/>).</summary>
public enum LinkDecision : byte
{
    /// <summary>Proposed, waiting for the user (or for "accept exact automatically").</summary>
    None,
    /// <summary>Accepted: Spotify's info shows for these files in Wavee (the files are untouched).</summary>
    Accepted,
    /// <summary>"Not this album" / "Not this song": the rejected candidate never comes back; the group waits for a pick.</summary>
    NotThis,
    /// <summary>"Not on Spotify": never matched again; the songs carry the mark.</summary>
    NotOnSpotify,
}

/// <summary>One Spotify track as the catalogue answered it. <paramref name="Artists"/> is the credit line ("A, B").</summary>
public sealed record LinkTrack(string Uri, string Title, string Artists, string Album, string AlbumUri, int DurationMs, string Isrc,
    int Disc, int Number, string? Cover, int Year);

/// <summary>One Spotify album with its track list (empty until its tracks were read).</summary>
public sealed record LinkAlbum(string Uri, string Title, string Artist, int Year, string? Cover, IReadOnlyList<LinkTrack> Tracks);

/// <summary>One proposal for a group, and the plain evidence for it.</summary>
/// <param name="Hits">How many of <paramref name="Of"/> songs carry the evidence ("11 of 11").</param>
/// <param name="DeltaMs">A song's length against the file's (Spotify minus file); 0 for an album.</param>
/// <param name="Uri">The Spotify album (an album group) or track (a song group).</param>
/// <param name="Detail">The line under its name: "Mia Solace · Blue Hour · 4:33", "11 songs · 2021".</param>
/// <param name="Tracks">Per file of the group, in the group's order, the Spotify track it maps to (null: none on that album).</param>
public sealed record LinkCandidate(LinkConfidence Confidence, LinkEvidence Evidence, int Hits, int Of, int DeltaMs, string Uri, string Title,
    string Artist, string Detail, string? Cover, int DurationMs, int Year, IReadOnlyList<LinkTrack?> Tracks);

/// <summary>A field of the review's Details table: what the file says, what Spotify says, and whether Spotify's improves it.</summary>
public readonly record struct LinkField(LinkFieldKind Kind, string Mine, string Theirs, bool Improves, string? MineCover = null, string? TheirCover = null);

/// <summary>The rows of the Details table (board 29).</summary>
public enum LinkFieldKind : byte { Title, Artist, AlbumArtist, Year, TrackNumbers, Cover }

/// <summary>One group of files the matcher answers for: an album (album first, concept §6.3) or a song with no album tag.</summary>
/// <param name="Key">The album's key (<see cref="LocalCollection.AlbumKey"/>) or "song:&lt;LocalId&gt;".</param>
/// <param name="Title">The album's (or song's) name as the files give it.</param>
/// <param name="Detail">"11 songs · FLAC 16/44.1", or for a song "Single song · 3:12 · MP3 320 · folder Mia Solace".</param>
/// <param name="FileCover">The files' own cover (a file uri), or null.</param>
/// <param name="Files">The group's files, in album order.</param>
/// <param name="Candidates">Best first; rejected ones are left out. Empty: nothing found ("Not on Spotify?").</param>
/// <param name="SpotifyWins">"Whose info wins for this album": Spotify's (true, the default) or the files'.</param>
public sealed record LinkGroup(string Key, bool IsAlbum, string Title, string Artist, string Detail, string? FileCover, IReadOnlyList<long> Files,
    IReadOnlyList<LinkCandidate> Candidates, LinkDecision Decision, bool SpotifyWins, IReadOnlyList<LinkField> Fields)
{
    /// <summary>The proposal the group shows (the best candidate), or null.</summary>
    public LinkCandidate? Best => Candidates.Count > 0 ? Candidates[0] : null;

    /// <summary>The group's confidence (Check when nothing was found or the user said "Not this").</summary>
    public LinkConfidence Confidence => Decision == LinkDecision.NotThis || Best is null ? LinkConfidence.Check : Best.Confidence;

    /// <summary>How many fields Spotify's info improves ("4 fields improve").</summary>
    public int Improves { get { int n = 0; foreach (var f in Fields) if (f.Improves) n++; return n; } }
}

/// <summary>Where matching stands.</summary>
public enum LinkPhase : byte
{
    /// <summary>The user has not turned matching on (the banner may offer it).</summary>
    Off,
    /// <summary>Matching runs in the background ("Matching · 12 of 48 albums").</summary>
    Matching,
    /// <summary>Matching ran; the groups wait in the review (or are decided).</summary>
    Ready,
}

/// <summary>Everything the review and the page read, as one immutable value (a new instance per change).</summary>
public sealed record LinkView
{
    public int Version { get; init; }
    public LinkPhase Phase { get; init; }
    /// <summary>While matching: groups done and the total.</summary>
    public int Done { get; init; }
    public int Total { get; init; }
    /// <summary>Every group, albums first (best confidence first), then songs.</summary>
    public IReadOnlyList<LinkGroup> Groups { get; init; } = [];
    /// <summary>Undecided groups per confidence (the review's chips and the "Matching finished" toast).</summary>
    public int Exact { get; init; }
    public int Strong { get; init; }
    public int Likely { get; init; }
    public int Check { get; init; }
    public int Accepted { get; init; }
    /// <summary>Groups "Find on Spotify…" is matching now that have no answer yet (the review says "Looking for it on Spotify…").</summary>
    public IReadOnlyList<string> Finding { get; init; } = [];

    /// <summary>Undecided groups.</summary>
    public int Pending => Exact + Strong + Likely + Check;

    public static LinkView Empty { get; } = new();
}
