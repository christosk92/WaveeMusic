using System.Text.Json;
using System.Text.RegularExpressions;

namespace Wavee.Module.YouTube;

/// <summary>
/// What one <c>/player</c> response means, as ONE closed set. Before this existed the module asked two boolean
/// predicates in a fixed order (<c>IsBotWall</c> then <c>IsAgeGate</c>) and the age predicate answered true for ANY
/// bare <c>LOGIN_REQUIRED</c> — correct only because the bot test happened to run first. Reordering those two lines
/// silently reclassified every bot wall as an age gate, which is TERMINAL (<c>NeedsAuth</c>, no next client). The
/// ordering now lives inside <see cref="YouTubeWallPolicy.Classify"/> where it can be read and tested.
/// </summary>
public enum PlayabilityVerdict
{
    /// <summary>YouTube will serve this video to this client.</summary>
    Ok,

    /// <summary>A sign-in wall that reads as ordinary rate limiting: one client refused, or the device has only just
    /// started seeing walls. Reported to the app as <c>Transient</c>.</summary>
    BotWallRetryable,

    /// <summary>A sign-in wall that reads as a sustained challenge: every client asked was walled, or the device has
    /// been walled repeatedly already. Reported to the app as <c>Unavailable</c>.</summary>
    BotWallBlocked,

    /// <summary>An age gate, positively identified. Terminal: no other client can get past it.</summary>
    AgeGate,

    /// <summary>A broadcast that is not on air. Still playable when the response carried an HLS master (the DVR
    /// window right after a broadcast ends), which is why the caller checks the manifest before believing it.</summary>
    Offline,

    /// <summary>Any other refusal ("made for kids", "not available on this app", an empty status). The reason is
    /// shown verbatim once every client has said it.</summary>
    Unplayable,
}

/// <summary>
/// Reads a <c>/player</c> playability block and decides how hard to push back. Pure: every input is a value and the
/// same inputs always give the same verdict, so the whole policy is unit-testable without a transport.
/// <para>
/// The evidence it is sized against, kept here because it is what makes the numbers defensible rather than tasteful:
/// on 2026-08-22 the wall was observed to be per-CLIENT — VISIONOS answered <c>LOGIN_REQUIRED</c> "confirm you're not
/// a bot" for a live stream ANDROID then served from the SAME IP. On 2026-08-23, under load from one long-lived
/// module process, that stopped holding: VISIONOS was walled on 9 of 9 attempts (ANDROID served each time), and once
/// the address was hot all three clients walled together — 5 x ANDROID, 5 x IOS — repeatedly for about 38 minutes.
/// So a wall is neither "try everything" nor "give up": it is worth exactly ONE alternate client, and repeating it is
/// worth backing off from.
/// </para>
/// </summary>
public static partial class YouTubeWallPolicy
{
    /// <summary>The status every sign-in wall carries, age gates included.</summary>
    public const string LoginRequiredStatus = "LOGIN_REQUIRED";

    /// <summary>The reason string YouTube words the bot wall with today. Used to describe a wall the module already
    /// knows about (a cooldown it is serving), never to detect one — <see cref="Classify"/> does not require it.</summary>
    public const string BotWallReason = "Sign in to confirm you're not a bot";

    /// <summary>
    /// What the user is told about a <see cref="PlayabilityVerdict.BotWallRetryable"/>. Deliberately says only what
    /// was actually observed: the device is being throttled and time fixes it.
    /// </summary>
    public const string RetryableMessage = "YouTube is rate-limiting this device. Try again in a minute.";

    /// <summary>
    /// What the user is told about a <see cref="PlayabilityVerdict.BotWallBlocked"/>. It names the one correlation
    /// the research supports (shared/VPN exit addresses are challenged more often) WITHOUT asserting the user's
    /// network is one, and without promising that signing in helps — nobody has ever validated that.
    /// </summary>
    public const string BlockedMessage =
        "YouTube is challenging this device as a bot. This usually clears on its own; a VPN or shared connection " +
        "makes it more likely.";

    /// <summary>How many clients a single walk must have asked before "they all walled" is allowed to mean blocked
    /// rather than one client's bad luck.</summary>
    public const int BlockedWhenClientsWalled = 2;

    /// <summary>How many consecutive walled walks the device may have behind it before even a single-client wall
    /// reads as blocked rather than retryable.</summary>
    public const int BlockedAfterConsecutiveWalks = 2;

    /// <summary>
    /// Classifies one <c>/player</c> playability block.
    /// <para>
    /// The order is the contract. The age gate is tested FIRST and only ever on POSITIVE evidence — the
    /// <c>desktopLegacyAgeGateReason</c> marker, an explicit age status, or an age-worded reason. Everything that is
    /// still <c>LOGIN_REQUIRED</c> after that is a wall, because "sign in" with no age evidence is the wall family:
    /// YouTube has reworded it repeatedly and a missing or unfamiliar wording must not promote it to the terminal
    /// verdict. That is the inversion of the old incidental ordering, and it is why this function exists.
    /// </para>
    /// </summary>
    /// <param name="status"><c>playabilityStatus.status</c>, or null when the response carried none.</param>
    /// <param name="reason"><c>playabilityStatus.reason</c>, or null.</param>
    /// <param name="hasAgeGateFlag">True when <c>playabilityStatus.desktopLegacyAgeGateReason</c> was present. No bot
    /// wall has ever been observed carrying it, which is what makes testing it first safe.</param>
    /// <param name="clientsWalled">How many EARLIER clients in this same walk were walled — this response excluded,
    /// because whether it is itself a wall is what is being decided.</param>
    /// <param name="clientsTried">How many clients have been asked in this walk INCLUDING the one that produced this
    /// response (so 1 for the first). Zero means no request was made at all: the caller is describing a cooldown it
    /// is already serving.</param>
    /// <param name="recentWallsInWindow">How many consecutive walks have already ended walled. Carries the escalation
    /// across plays, which is the only way a per-response function can tell "one unlucky client" from "this device is
    /// being challenged".</param>
    public static PlayabilityVerdict Classify(string? status, string? reason, bool hasAgeGateFlag,
        int clientsWalled, int clientsTried, long recentWallsInWindow)
    {
        // 1. AGE GATE — positive evidence only, never inferred from LOGIN_REQUIRED alone.
        if (hasAgeGateFlag) return PlayabilityVerdict.AgeGate;
        if (status is "AGE_CHECK_REQUIRED" or "AGE_VERIFICATION_REQUIRED") return PlayabilityVerdict.AgeGate;
        if (reason is not null && AgeReason().IsMatch(reason)) return PlayabilityVerdict.AgeGate;

        // 2. WALL — every remaining sign-in demand, however it happens to be worded this month.
        if (string.Equals(status, LoginRequiredStatus, StringComparison.Ordinal))
        {
            return IsBlocked(clientsWalled, clientsTried, recentWallsInWindow)
                ? PlayabilityVerdict.BotWallBlocked
                : PlayabilityVerdict.BotWallRetryable;
        }

        // 3. Everything else is about the VIDEO, not about us.
        if (string.Equals(status, "LIVE_STREAM_OFFLINE", StringComparison.Ordinal)) return PlayabilityVerdict.Offline;
        if (string.Equals(status, "OK", StringComparison.Ordinal)) return PlayabilityVerdict.Ok;
        return PlayabilityVerdict.Unplayable;
    }

    /// <summary>
    /// How long to refuse to touch YouTube at all after a walk ended walled. Four steps and then a ceiling: the point
    /// is to make a user mashing Play cost ZERO requests, not to punish them, so the first wall costs half a minute
    /// and the cap stays well inside the ~38-minute window the 2026-08-23 session spent walled.
    /// </summary>
    /// <param name="consecutiveWalls">How many walks in a row have ended walled, this one included. Zero or less is
    /// "nothing has gone wrong", which is no cooldown at all.</param>
    public static long CooldownMsFor(int consecutiveWalls) => consecutiveWalls switch
    {
        <= 0 => 0,
        1 => 30_000,
        2 => 120_000,
        _ => 300_000,
    };

    /// <summary>Whether a wall reads as a sustained challenge rather than one client's bad luck.</summary>
    /// <param name="clientsWalled">Earlier walls in this walk.</param>
    /// <param name="clientsTried">Clients asked so far, this response included.</param>
    /// <param name="recentWallsInWindow">Consecutive walks already ended walled.</param>
    private static bool IsBlocked(int clientsWalled, int clientsTried, long recentWallsInWindow)
    {
        // The device has been here before: one client refusing is no longer news.
        if (recentWallsInWindow >= BlockedAfterConsecutiveWalks) return true;

        // Every client asked in THIS walk was walled, and at least two were asked. One client walling while another
        // serves is the 2026-08-22 shape and stays retryable; all of them walling together is the 2026-08-23 shape.
        return clientsTried >= BlockedWhenClientsWalled && clientsWalled + 1 >= clientsTried;
    }

    [GeneratedRegex("confirm your age|age-restricted|age restricted|inappropriate",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AgeReason();
}

/// <summary>
/// What the module remembers about ITSELF between plays and between runs, in the module's own data dir. All three
/// members exist to stop the module presenting as a brand-new anonymous client on every single request, which is
/// exactly the shape an anti-bot system is looking for.
/// </summary>
/// <param name="VisitorData">The <c>responseContext.visitorData</c> InnerTube hands back on every response and expects
/// echoed on the next one, as both <c>X-Goog-Visitor-Id</c> and <c>context.client.visitorData</c>. Dropped the moment
/// a request carrying it is walled: a burned visitor id is worse than none.</param>
/// <param name="PreferredClientKey">The client key that last produced a playable manifest, tried FIRST on the next
/// resolve. The single highest-value entry here: the table order put VISIONOS first, and on 2026-08-23 VISIONOS was
/// walled on 9 of 9 attempts before ANDROID served — so every play burned one flagged request before it started.</param>
/// <param name="WalledUntilUnixMs">Unix ms before which the module issues NO request at all. Persisted, not just held
/// in memory, so restarting the app is not a way to keep hammering.</param>
public sealed record YouTubeSession(string? VisitorData, string? PreferredClientKey, long WalledUntilUnixMs)
{
    /// <summary>A session that has learned nothing yet: what a missing or unreadable file yields.</summary>
    public static YouTubeSession Empty { get; } = new(null, null, 0);
}

/// <summary>
/// Loads and saves <see cref="YouTubeSession"/> as <c>session.json</c> in the module's data dir — the same writable,
/// permission-free directory <c>clients.json</c> is already overridden from. Nothing in it is a secret: a visitor id
/// is an anonymous session token YouTube itself hands out unauthenticated on every response.
/// </summary>
public static class YouTubeSessionStore
{
    /// <summary>The file name inside the data dir.</summary>
    public const string FileName = "session.json";

    /// <summary>
    /// Reads the session, or <see cref="YouTubeSession.Empty"/> when there is nothing readable to read. NEVER throws,
    /// for any reason — this is a cache of conveniences, and no failure to read it is worth failing a play for, so
    /// the catch is deliberately total rather than a list of the exception types that happen to be likely.
    /// </summary>
    /// <param name="dataDir">The module's data dir.</param>
    public static YouTubeSession Load(string dataDir)
    {
        if (string.IsNullOrWhiteSpace(dataDir)) return YouTubeSession.Empty;

        try
        {
            string path = Path.Combine(dataDir, FileName);
            if (!File.Exists(path)) return YouTubeSession.Empty;
            return JsonSerializer.Deserialize(File.ReadAllBytes(path), YouTubeJsonContext.Default.YouTubeSession)
                   ?? YouTubeSession.Empty;
        }
        catch
        {
            return YouTubeSession.Empty;
        }
    }

    /// <summary>Writes the session, creating the data dir if needed. NEVER throws, for the same reason as
    /// <see cref="Load"/>: losing the file costs one flagged request, and throwing costs the play.</summary>
    /// <param name="dataDir">The module's data dir.</param>
    /// <param name="session">The session to persist.</param>
    public static void Save(string dataDir, YouTubeSession session)
    {
        if (string.IsNullOrWhiteSpace(dataDir) || session is null) return;

        try
        {
            Directory.CreateDirectory(dataDir);
            File.WriteAllBytes(Path.Combine(dataDir, FileName),
                JsonSerializer.SerializeToUtf8Bytes(session, YouTubeJsonContext.Default.YouTubeSession));
        }
        catch
        {
            // Deliberately silent: see the type doc.
        }
    }
}
