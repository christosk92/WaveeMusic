namespace Wavee.Core.Audio;

// Recoverable failures from the PlayPlay path are surfaced as
// PlayPlayHelperException; callers log and re-throw the original AP failure.
public sealed class PlayPlayHelperException : Exception
{
    public PlayPlayHelperException(string message) : base(message) { }
    public PlayPlayHelperException(string message, Exception inner) : base(message, inner) { }
}
