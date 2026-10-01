// Spotify.Audio.KeyBook.cs — what the key seam has already been told about a FILE, as one engine-free class.
//
// The measured defect (a capture of 34 key POSTs over 20 files): 18 of them, 53 %, were 403s for four FLAC files, each
// retried three times by TWO concurrent callers (the prefetch of the next track and the open of it), the retries
// re-sending the first attempt's timestamp, and nothing remembering that the file had said no — so a refused lossless
// track cost 6-9 s of backoff before it played its Ogg 320 rung. A 403 is a REFUSAL about that file, not a hiccup:
// this book makes the refusal cost one request, once per cool-down, shared by every caller, and keeps the
// retry-with-backoff for what is actually transient (5xx, transport).

namespace Wavee;

public static partial class Spotify
{
    public static partial class Audio
    {
        /// <summary>The process's one book, read by the seam's deriver. Reset with the key latch (a new AP session may
        /// be a new account).</summary>
        public static readonly KeyOutcomeBook KeyBook = new();

        /// <summary>Per-file answers to "can this file's key be had": single-flight (concurrent callers for one file
        /// share one in-flight attempt and its result), a negative cache for a refused file, and a bounded retry for
        /// transient failures. PURE apart from the injected clock and backoff, so every rule is unit-tested.</summary>
        public sealed class KeyOutcomeBook
        {
            /// <summary>How an attempt ended.</summary>
            public enum Verdict : byte
            {
                /// <summary>The key was had.</summary>
                Ok,
                /// <summary>The server REFUSED this file (a 403): asking again gets the same answer. Not retried, and
                /// remembered for <see cref="RefusedCooldownMs"/>.</summary>
                Refused,
                /// <summary>A transport error or a 5xx: retried with backoff, never remembered.</summary>
                Transient,
                /// <summary>Failed for a reason retrying cannot change (the derivation itself): not retried, not
                /// remembered here (the caller keeps its own back-off).</summary>
                Failed,
            }

            /// <summary>One attempt's answer. <see cref="Key"/> is non-null exactly for <see cref="Verdict.Ok"/>.</summary>
            public readonly record struct Outcome(Verdict Verdict, byte[]? Key = null)
            {
                public static Outcome Of(byte[] key) => new(Verdict.Ok, key);
                public static readonly Outcome Refused = new(Verdict.Refused);
                public static readonly Outcome Transient = new(Verdict.Transient);
                public static readonly Outcome Failed = new(Verdict.Failed);
            }

            /// <summary>How long a refused file is not asked again — per FILE ID, never to any other file.</summary>
            public const long RefusedCooldownMs = 10 * 60_000;

            /// <summary>Attempts for a transient failure (the first included).</summary>
            public const int MaxAttempts = 3;

            /// <summary>The wait before attempt <paramref name="nextAttempt"/> (1-based, so 2 and 3): 1 s, then 2 s.</summary>
            public static int BackoffMs(int nextAttempt) => 1000 << Math.Max(0, nextAttempt - 2);

            const int RefusedMax = 256;

            readonly Func<long> _nowMs;
            readonly Action<int, CancellationToken> _sleep;
            readonly Lock _gate = new();
            readonly Dictionary<string, long> _refusedUntil = new(StringComparer.OrdinalIgnoreCase);
            readonly Queue<string> _refusedOrder = new();
            readonly Dictionary<string, Flight> _flights = new(StringComparer.OrdinalIgnoreCase);

            sealed class Flight
            {
                public readonly ManualResetEventSlim Done = new(false);
                public Outcome Result = Outcome.Transient;
            }

            /// <param name="nowMs">A monotonic millisecond clock; null reads <see cref="Environment.TickCount64"/>.</param>
            /// <param name="sleep">Waits the backoff (milliseconds) and throws <see cref="OperationCanceledException"/>
            /// when cancelled; null waits on the token.</param>
            public KeyOutcomeBook(Func<long>? nowMs = null, Action<int, CancellationToken>? sleep = null)
            {
                _nowMs = nowMs ?? (static () => Environment.TickCount64);
                _sleep = sleep ?? (static (ms, ct) => { if (ct.WaitHandle.WaitOne(ms)) ct.ThrowIfCancellationRequested(); });
            }

            /// <summary>Is this file inside a refusal's cool-down?</summary>
            public bool IsRefused(string fileId)
            {
                lock (_gate) return RefusedLocked(fileId);
            }

            bool RefusedLocked(string fileId)
            {
                if (!_refusedUntil.TryGetValue(fileId, out long until)) return false;
                if (_nowMs() < until) return true;
                _refusedUntil.Remove(fileId);
                return false;
            }

            /// <summary>Forget every answer (a new AP session).</summary>
            public void Clear()
            {
                lock (_gate) { _refusedUntil.Clear(); _refusedOrder.Clear(); }
            }

            /// <summary>The outcome for <paramref name="fileId"/>. A refused file answers <see cref="Outcome.Refused"/>
            /// without calling <paramref name="attempt"/>; a file already in flight makes this caller wait for, and
            /// share, that flight's result; otherwise this caller runs <paramref name="attempt"/> (given the 1-based
            /// attempt number, and called ONCE PER ATTEMPT so the request it builds carries a fresh timestamp) until
            /// it is not <see cref="Verdict.Transient"/> or <see cref="MaxAttempts"/> is spent. A
            /// <see cref="Verdict.Refused"/> answer stops at once and starts the cool-down.</summary>
            public Outcome Run(string fileId, Func<int, CancellationToken, Outcome> attempt, CancellationToken ct)
            {
                Flight? joined;
                Flight? mine = null;
                lock (_gate)
                {
                    if (RefusedLocked(fileId)) return Outcome.Refused;
                    if (!_flights.TryGetValue(fileId, out joined)) { mine = new Flight(); _flights[fileId] = mine; }
                }

                if (joined is not null)
                {
                    joined.Done.Wait(ct);
                    return joined.Result;
                }

                Outcome result = Outcome.Transient;
                try
                {
                    result = Attempts(fileId, attempt, ct);
                    return result;
                }
                finally
                {
                    lock (_gate)
                    {
                        _flights.Remove(fileId);
                        if (result.Verdict == Verdict.Refused) RememberRefusedLocked(fileId);
                    }
                    mine!.Result = result;          // a thrown/cancelled leader leaves Transient for its followers
                    mine.Done.Set();
                }
            }

            Outcome Attempts(string fileId, Func<int, CancellationToken, Outcome> attempt, CancellationToken ct)
            {
                Outcome last = Outcome.Transient;
                for (int n = 1; n <= MaxAttempts; n++)
                {
                    ct.ThrowIfCancellationRequested();
                    last = attempt(n, ct);
                    if (last.Verdict != Verdict.Transient) return last;
                    if (n < MaxAttempts) _sleep(BackoffMs(n + 1), ct);
                }
                return last;
            }

            void RememberRefusedLocked(string fileId)
            {
                if (!_refusedUntil.ContainsKey(fileId)) _refusedOrder.Enqueue(fileId);
                _refusedUntil[fileId] = _nowMs() + RefusedCooldownMs;
                while (_refusedOrder.Count > RefusedMax && _refusedOrder.TryDequeue(out string? oldest)) _refusedUntil.Remove(oldest);
            }
        }
    }
}
