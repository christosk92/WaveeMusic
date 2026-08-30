namespace Wavee.Backend.Playback;

/// <summary>Clock formatting for the transport's time labels — the ONE place a millisecond count becomes the
/// <c>m:ss</c> / <c>h:mm:ss</c> string the player bar, the immersive stage and the queue all read.
///
/// <para>It lives under <c>Backend/</c>, engine-free, for one reason: <c>PlayerBar.cs</c> is engine-bound and is not
/// source-included by <c>Wavee.Tests</c>, so formatting authored there could never be pinned by a test. The hours rung
/// is exactly the case that needed pinning — a live broadcast's elapsed-since-tune-in crosses 59:59 on any stream you
/// leave running, and the old <c>m:ss</c>-only formatter answered "73:04" for it.</para>
///
/// <para>Digits are written by hand rather than through a composite format string: these labels are rebuilt every
/// second on the position tick, and the transport is the one surface where a per-tick boxed <c>long</c> and a
/// culture lookup would be paid forever. The separator is the invariant colon (every locale the app ships writes a
/// clock duration with it).</para></summary>
public static class TimeFormat
{
    /// <summary>One hour, in ms — the rung where the label grows an hours field.</summary>
    public const long HourMs = 3_600_000L;

    /// <summary>A duration as a transport clock: <c>m:ss</c> below one hour, <c>h:mm:ss</c> at or above it (the minutes
    /// field takes a leading zero only once hours are present, which is what makes 1:05:07 read as an hour and not as
    /// "one minute five"). Negative input clamps to 0 — a clock never counts backwards.</summary>
    public static string Clock(long ms)
    {
        if (ms < 0L) ms = 0L;
        long totalSeconds = ms / 1000L;
        long s = totalSeconds % 60L;
        long totalMinutes = totalSeconds / 60L;
        if (ms < HourMs) return totalMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + Two(s);
        long m = totalMinutes % 60L;
        long h = totalMinutes / 60L;
        return h.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + Two(m) + ":" + Two(s);
    }

    static string Two(long v) => v < 10L
        ? "0" + v.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : v.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
