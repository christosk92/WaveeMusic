namespace Wavee.Core;

// Production error-handling, channel 1: typed Result for EXPECTED failures (not-found, offline, auth-expired, …).
// Framework-neutral + AOT-clean. Exceptions remain channel 2 (the genuinely exceptional). The UI resolves a Result
// at the boundary via Match(...), turning the error branch into a friendly inline state (see Components/ErrorState).

/// <summary>A semantic, user-surfaceable failure. <see cref="Code"/> is stable (telemetry / branching);
/// <see cref="Message"/> is human-friendly.</summary>
public sealed record Error(string Code, string Message)
{
    public override string ToString() => $"{Code}: {Message}";
}

/// <summary>The outcome of an operation that can fail in an expected way: either a value or an <see cref="Error"/>.</summary>
public readonly record struct Result<T>
{
    readonly T _value;
    public Error? Error { get; }
    public bool IsOk => Error is null;
    public bool IsError => Error is not null;

    Result(T value) { _value = value; Error = null; }
    Result(Error error) { _value = default!; Error = error; }

    public static Result<T> Ok(T value) => new(value);
    public static Result<T> Fail(Error error) => new(error);

    public T Value => IsOk ? _value : throw new InvalidOperationException($"Result is an error: {Error}");
    public bool TryGet(out T value) { value = _value!; return IsOk; }
    public T ValueOr(T fallback) => IsOk ? _value : fallback;

    public TOut Match<TOut>(Func<T, TOut> onOk, Func<Error, TOut> onErr) => IsOk ? onOk(_value) : onErr(Error!);
    public void Match(Action<T> onOk, Action<Error> onErr) { if (IsOk) onOk(_value); else onErr(Error!); }
}

/// <summary>Non-generic helpers so call sites read <c>Result.Ok(x)</c> / <c>Result.Fail&lt;T&gt;(e)</c>.</summary>
public static class Result
{
    public static Result<T> Ok<T>(T value) => Result<T>.Ok(value);
    public static Result<T> Fail<T>(Error error) => Result<T>.Fail(error);
}

// ── Per-domain error catalogs (stable codes + friendly copy) ────────────────────────────────────────────────────────
public static class AuthErrors
{
    public static readonly Error NotAuthenticated = new("auth.not_authenticated", "You're not signed in.");
    public static readonly Error LoginFailed = new("auth.login_failed", "Sign-in failed. Please try again.");
}

public static class LibraryErrors
{
    public static readonly Error NotFound = new("library.not_found", "We couldn't find that.");
    public static readonly Error Offline = new("library.offline", "You're offline — showing what we have.");
    public static readonly Error LoadFailed = new("library.load_failed", "Something went wrong loading this.");
}

public static class PlaybackErrors
{
    public static readonly Error NoDevice = new("playback.no_device", "No playback device is available.");
    public static readonly Error Unavailable = new("playback.unavailable", "This track can't be played right now.");
}
