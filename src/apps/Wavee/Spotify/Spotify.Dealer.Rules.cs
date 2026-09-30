// ── Spotify/Spotify.Dealer.Rules.cs ──────────────────────────────────────────────────────────────────────────────
// The dealer's pusher-hello deadline, the one rule that recognises the hello, and the dealer topic as the log may
// print it. PURE — no clock, no socket; the dealer thread (`Spotify.Session.cs` §11) passes the clock in.
//
// Why (docs/plans/wavee/dealer-hello-rca.md, 2026-09-30): a dealer socket is "open" when the wss handshake completes
// and "registered" when the pusher's hello arrives; only the second makes the session Online. A node accepted the
// socket, answered every keepalive for ten minutes and never sent the hello, and nothing bounded `LinkPhase.Opening`:
// the session sat on "Connecting…" with every Spotify fetch held. The deadline below drops such a socket through the
// dealer's existing drop → backoff → retry path.

using System.Text;

namespace Wavee;

public static partial class Spotify
{
    /// <summary>The pusher hello deadline. A dealer socket is "open" when the wss handshake completes and "registered"
    /// when the pusher's hello (<c>hm://pusher/v1/connections/&lt;id&gt;</c> + <c>Spotify-Connection-Id</c>) arrives; only
    /// the second makes the session Online (<see cref="Step"/>'s DealerOnline case). 2026-09-30
    /// (<c>docs/plans/wavee/dealer-hello-rca.md</c>): a node accepted the socket, answered every keepalive for ten minutes
    /// and never sent the hello, and nothing bounded <see cref="LinkPhase.Opening"/>. Observed hello latency on good
    /// sockets is 8 ms – 1.7 s; the deadline is five times the slowest and a third of the first keepalive. PURE — no
    /// clock, no socket.</summary>
    public static class DealerHelloRules
    {
        public const int HelloDeadlineMs = 10_000;

        /// <summary>How many of a socket's first frames are logged one line each (<c>dealer.frame</c>): enough to see the
        /// hello, or to see what came instead of it.</summary>
        public const int FirstFramesLogged = 3;

        public enum HelloVerdict : byte { Waiting = 0, Held = 1, Overdue = 2 }

        public static HelloVerdict Verdict(bool helloHeld, long connectedAtMs, long nowMs)
            => helloHeld ? HelloVerdict.Held
             : nowMs - connectedAtMs >= HelloDeadlineMs ? HelloVerdict.Overdue
             : HelloVerdict.Waiting;

        /// <summary>How long ONE receive may block: the remaining hello budget until the hello is held, unbounded
        /// (<see cref="Timeout.Infinite"/>) afterwards, 0 when the deadline has already passed.</summary>
        public static int ReceiveBudgetMs(bool helloHeld, long connectedAtMs, long nowMs)
        {
            if (helloHeld) return Timeout.Infinite;
            long left = HelloDeadlineMs - (nowMs - connectedAtMs);
            return left <= 0 ? 0 : (int)Math.Min(left, int.MaxValue);
        }

        /// <summary>The ONE rule <c>Dispatch</c> applies to recognise the hello, named so a test can pin it against the
        /// captured wire shape: a MESSAGE (or REQUEST) on a <c>hm://pusher/</c> topic that carries a connection id.</summary>
        public static bool IsHello(DealerFrameKind kind, ReadOnlySpan<byte> uri, ReadOnlySpan<byte> connectionId)
            => kind is DealerFrameKind.Message or DealerFrameKind.Request
            && !connectionId.IsEmpty
            && uri.StartsWith("hm://pusher/"u8);

        /// <summary>The <c>label=</c> of a <c>dealer.frame</c> log line: <c>ping</c>/<c>pong</c>, the topic of a MESSAGE,
        /// <c>req:</c> + the ident of a REQUEST, <c>(unknown)</c> otherwise — each topic through <see cref="LogTopic"/>,
        /// so the line never carries a connection id or an account name.</summary>
        public static string LogLabel(DealerFrameKind kind, ReadOnlySpan<byte> uri, ReadOnlySpan<byte> ident)
        {
            if (kind == DealerFrameKind.Ping) return "ping";
            if (kind == DealerFrameKind.Pong) return "pong";
            if (!uri.IsEmpty) return LogTopic(uri);
            if (!ident.IsEmpty) return "req:" + LogTopic(ident);
            return "(unknown)";
        }

        /// <summary>A dealer topic as the always-on log may print it: <c>hm://</c>, then its path segments while each is
        /// a plain topic word (a lowercase letter, then up to 23 of <c>[a-z0-9-_]</c>); the first segment that is not —
        /// or that the topic's own shape says is an identifier: whatever follows <c>connections</c> (the pusher's
        /// connection id) or <c>user</c> (an account name), and the third segment of <c>hm://collection/&lt;set&gt;/&lt;user&gt;</c>
        /// — is cut, with everything after it, to <c>…</c>. So <c>hm://pusher/v1/connections/&lt;id&gt;</c> logs as
        /// <c>hm://pusher/v1/connections/…</c> and <c>hm://connect-state/v1/cluster</c> whole. Anything that is not an
        /// <c>hm://</c> topic logs as <c>(opaque)</c>.</summary>
        public static string LogTopic(ReadOnlySpan<byte> topic)
        {
            if (topic.IsEmpty) return "(none)";
            ReadOnlySpan<byte> scheme = "hm://"u8;
            if (!topic.StartsWith(scheme)) return "(opaque)";

            int end = scheme.Length;          // topic[..end] is printed verbatim
            int at = scheme.Length, index = 0;
            bool collection = false;
            ReadOnlySpan<byte> previous = default;
            while (at < topic.Length)
            {
                var rest = topic[at..];
                int slash = rest.IndexOf((byte)'/');
                var word = slash < 0 ? rest : rest[..slash];
                bool identifier = previous.SequenceEqual("connections"u8) || previous.SequenceEqual("user"u8)
                               || (collection && index == 2);
                if (identifier || !IsTopicWord(word)) break;
                if (index == 0) collection = word.SequenceEqual("collection"u8);
                previous = word;
                index++;
                at += word.Length;
                end = at;
                if (slash < 0) break;
                at++;                         // the slash is printed with the word before it
                end = at;
            }
            return end >= topic.Length ? Encoding.UTF8.GetString(topic) : Encoding.UTF8.GetString(topic[..end]) + "…";
        }

        static bool IsTopicWord(ReadOnlySpan<byte> word)
        {
            if (word.IsEmpty || word.Length > 24 || word[0] is < (byte)'a' or > (byte)'z') return false;
            foreach (byte b in word)
                if (b is not ((>= (byte)'a' and <= (byte)'z') or (>= (byte)'0' and <= (byte)'9') or (byte)'-' or (byte)'_'))
                    return false;
            return true;
        }
    }

    /// <summary>The receive loop's word for "the deadline passed with no hello": an <see cref="IOException"/>, so
    /// <c>DealerLoopCore</c>'s existing drop path handles it, distinguishable so the fold gets
    /// <see cref="SessionFault.Protocol"/> and the exit line says <c>hello-overdue</c>.</summary>
    public sealed class DealerHelloOverdueException(int afterMs, int frames)
        : IOException("no pusher hello within " + afterMs + " ms (frames received: " + frames + ")")
    {
        public int AfterMs { get; } = afterMs;
        public int Frames { get; } = frames;
    }
}
