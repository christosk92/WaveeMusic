namespace Wavee;

/// <summary>The <c>--crash-probe [throw|failfast]</c> CLI arm's latch — a static, not an environment variable (the
/// repo forbids env-var behaviour switches), set once in <c>Program.cs</c> and consumed by <c>ReportChrome</c>,
/// which arms a 2-second <c>UseTimeout</c> that either throws or calls <see cref="System.Environment.FailFast"/> so
/// the crash-prompt path (managed report vs. WER dump vs. unclean exit) can be rehearsed end to end.</summary>
static class CrashProbe
{
    public static string? Mode;
}
