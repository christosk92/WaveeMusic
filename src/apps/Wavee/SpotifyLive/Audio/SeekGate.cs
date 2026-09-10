namespace Wavee.SpotifyLive.Audio;

/// <summary>What the audio host does with a seek request, decided against the state of the serialized session pump
/// at the moment the seek op runs (never earlier — the answer depends on ops that ran before it).</summary>
internal enum SeekAdmission
{
    /// <summary>Hand the seek to the engine now: a session is open and its byte source can serve the target.</summary>
    ApplyNow,
    /// <summary>Park the target and apply it after the next body attach / session open — the engine's seek would
    /// otherwise block the pump waiting for bytes that only a LATER pump op can attach.</summary>
    Defer,
}

/// <summary>Pure decision behind <c>FluentMediaAudioHost.Seek</c>. Exists because of one deadlock: the controller
/// enqueues <c>LoadFastStart</c> → <c>Seek(resumePositionMs)</c> → (later) <c>SupplyBody</c> onto ONE serialized
/// pump, and the 0.2.9 engine's <c>PcmAudioSession.SeekAsync</c> holds its replacement gate until the decode producer
/// has PCM at the target. A fast-start session owns only the ~80 KB clear head; a target beyond it makes the decoder
/// block in <c>SpotifyAudioStream.WaitForBody</c> — for the body attach that is the very next op in the pump, behind
/// the seek. Nothing ever completes; the UI shows an endless buffering bar until the next track opens a fresh session.
/// The 0.2.8 engine's seek returned immediately (a fire-and-forget worker request), which is why the shipped build never
/// hit it. A launch restore at a saved position and a video→audio media swap mid-track both take this path.</summary>
internal static class SeekGate
{
    /// <param name="hasSession">an engine session is open (a deferred-open load has none until its body attaches).</param>
    /// <param name="sourceCanServeBeyondHead">the active byte source can read past its clear head: a Spotify stream with
    /// its body attached, or a source that never had a head/body split (local file, external stream, module stream).</param>
    public static SeekAdmission Decide(bool hasSession, bool sourceCanServeBeyondHead)
        => hasSession && sourceCanServeBeyondHead ? SeekAdmission.ApplyNow : SeekAdmission.Defer;

    /// <summary>The position the host should REPORT while a seek is parked: the parked target — the session's own clock
    /// still reads the pre-seek position (0 for a fresh load), and publishing that would show 0:00 for a track the user
    /// resumed at 3:45. <paramref name="pendingSeekMs"/> &lt; 0 means nothing is parked.</summary>
    public static long ReportedPositionMs(long pendingSeekMs, long clockPositionMs)
        => pendingSeekMs >= 0 ? pendingSeekMs : clockPositionMs;
}
