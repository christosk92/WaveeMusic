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

    /// <summary>What a dealer REQUEST is answered with (2026-10-01 audit: every one of 15 requests in nine days was acked
    /// <c>success:true</c> before anything ran, unknown endpoints included). The ack says "received and understood as
    /// something this device does", never "it worked" — the execution happens after it, on the host. PURE.</summary>
    public static class DealerAck
    {
        /// <summary>The one <c>message_ident</c> a controller's command arrives under.</summary>
        public static ReadOnlySpan<byte> CommandIdent => "hm://connect-state/v1/player/command"u8;

        public static bool IsCommandIdent(ReadOnlySpan<byte> ident) => ident.SequenceEqual(CommandIdent);

        /// <summary>The reply's <c>success</c>. False for a request that is not a player command, and for an endpoint this
        /// device does not know (<see cref="Decode.RemoteCmd.Unknown"/>) — librespot answers Failure for both. TRUE for a
        /// KNOWN endpoint whose body was garbled: <c>Ok</c> is not consulted, because a failure reply there gets the sender
        /// cached out of ever sending that endpoint again (0.2.9, see <see cref="Decode.RemoteCommand"/>).</summary>
        public static bool For(ReadOnlySpan<byte> ident, Decode.RemoteCmd kind)
            => IsCommandIdent(ident) && kind != Decode.RemoteCmd.Unknown;
    }

    /// <summary>Recent REQUEST identities, so a controller's retry of a command we already ran is acked but not run twice
    /// (<see cref="Decode.RemoteCommand.DedupeKey"/> folds sender, message id and endpoint). Fixed size, allocation-free,
    /// thread-safe; the oldest key is overwritten.</summary>
    public sealed class DealerDedupeRing(int capacity = DealerDedupeRing.DefaultCapacity)
    {
        public const int DefaultCapacity = 32;

        readonly ulong[] _keys = new ulong[Math.Max(1, capacity)];
        readonly Lock _gate = new();
        int _next;

        /// <summary>A command that can be told apart from another: it names its sender and carries a message id. One that
        /// does not (an undecodable body, a controller that sends no id) would collide with every sibling, so it is never
        /// deduplicated.</summary>
        public static bool IsDedupable(in Decode.RemoteCommand command)
            => command.Kind != Decode.RemoteCmd.Unknown && command.MessageId != 0 && command.SenderHash != 0 && command.DedupeKey != 0;

        /// <summary>True when <paramref name="key"/> is among the recent ones (a duplicate); otherwise records it and
        /// answers false. Key 0 is the empty slot and is never recorded.</summary>
        public bool SeenOrAdd(ulong key)
        {
            if (key == 0) return false;
            lock (_gate)
            {
                foreach (ulong k in _keys) if (k == key) return true;
                _keys[_next] = key;
                _next = (_next + 1) % _keys.Length;
                return false;
            }
        }
    }

    /// <summary>A per-key counter that says when a line is worth writing: the first time a key is seen, then at most once
    /// per interval with the running total. For the frames and mailbox drops that are otherwise silent — one line per frame
    /// would flood the log, no line at all hides a protocol change. Bounded: past <c>capacity</c> distinct keys the rest
    /// share one <c>(other)</c> slot. Thread-safe.</summary>
    public sealed class DealerTally(int intervalMs, int capacity = 16)
    {
        public const string Other = "(other)";

        readonly string?[] _keys = new string?[Math.Max(2, capacity)];
        readonly long[] _totals = new long[Math.Max(2, capacity)];
        readonly long[] _lastLogged = new long[Math.Max(2, capacity)];
        readonly Lock _gate = new();

        /// <summary>The key's run ended (the link came up): the next sighting is a first one again and logs in full.</summary>
        public void Forget(string key)
        {
            lock (_gate)
            {
                for (int i = 0; i < _keys.Length - 1; i++)
                {
                    if (_keys[i] is null) return;
                    if (_keys[i] != key) continue;
                    _totals[i] = 0;
                    _lastLogged[i] = long.MinValue / 2;
                    return;
                }
            }
        }

        /// <summary>Count one occurrence of <paramref name="key"/>. True when the caller should log it now;
        /// <paramref name="total"/> is the count so far for that key.</summary>
        public bool Note(string key, long nowMs, out long total)
        {
            lock (_gate)
            {
                int slot = -1;
                for (int i = 0; i < _keys.Length - 1; i++)
                {
                    if (_keys[i] is null) { _keys[i] = key; _lastLogged[i] = nowMs - intervalMs - 1; slot = i; break; }
                    if (_keys[i] == key) { slot = i; break; }
                }
                if (slot < 0) { slot = _keys.Length - 1; _keys[slot] ??= Other; }
                total = ++_totals[slot];
                // `_lastLogged` starts a full interval in the past for a new key, so its first sighting logs.
                if (total > 1 && nowMs - _lastLogged[slot] < intervalMs) return false;
                _lastLogged[slot] = nowMs;
                return true;
            }
        }
    }

    /// <summary>At most one links kick (wake or address change) per gap: one adapter change fires several
    /// <c>NetworkAddressChanged</c> events, a flapping Wi-Fi fires them for as long as it flaps. PURE.</summary>
    public static class KickLimiter
    {
        public const int MinGapMs = 5_000;

        /// <summary>The "no kick yet" stamp: far enough in the past that any clock reads it as due.</summary>
        public const long Never = -1_000_000_000L;

        /// <summary>True when a kick at <paramref name="nowMs"/> may go out, the last one having gone at
        /// <paramref name="lastKickMs"/>. A clock that went backwards reads as due rather than silencing kicks.</summary>
        public static bool Due(long nowMs, long lastKickMs, int minGapMs = MinGapMs)
            => nowMs < lastKickMs || nowMs - lastKickMs >= minGapMs;
    }

    /// <summary>Which dealer MESSAGE topics this app knows it does not read. PURE.</summary>
    public static class DealerTopicRules
    {
        public enum Kind : byte
        {
            /// <summary>An <c>hm://</c> topic: the host's handlers (playback settings, progress, library) take it or leave it.</summary>
            Hm = 0,
            /// <summary>A topic without the <c>hm://</c> scheme (<c>social-connect/v2/broadcast_status_update</c>, 264 in nine
            /// days): never read by this app, counted so a new one is visible.</summary>
            NonHm,
            /// <summary><c>hm://playlist/v2/list/liked-songs-artist/&lt;id&gt;</c>: a push this app ignores on purpose.</summary>
            IgnoredOnPurpose,
        }

        public static Kind Classify(ReadOnlySpan<byte> uri)
        {
            if (!uri.StartsWith("hm://"u8)) return Kind.NonHm;
            if (uri.StartsWith("hm://playlist/v2/list/liked-songs-artist/"u8)) return Kind.IgnoredOnPurpose;
            return Kind.Hm;
        }

        /// <summary>A non-<c>hm://</c> topic as a counter key and the log may print it: its first three path segments while
        /// each is a plain topic word (<c>social-connect/v2/broadcast_status_update</c> whole), cut to <c>/…</c> at the first
        /// segment that is not — an id never reaches the always-on log.</summary>
        public static string LogKey(ReadOnlySpan<byte> uri)
        {
            if (uri.IsEmpty) return "(none)";
            int end = 0, at = 0;
            for (int segment = 0; segment < 3 && at <= uri.Length; segment++)
            {
                var rest = uri[at..];
                int slash = rest.IndexOf((byte)'/');
                var word = slash < 0 ? rest : rest[..slash];
                if (!IsWord(word)) break;
                end = at + word.Length;
                if (slash < 0) return Encoding.UTF8.GetString(uri);
                at = end + 1;
            }
            return end == 0 ? "(opaque)" : Encoding.UTF8.GetString(uri[..end]) + "/…";
        }

        static bool IsWord(ReadOnlySpan<byte> word)
        {
            if (word.IsEmpty || word.Length > 32 || word[0] is < (byte)'a' or > (byte)'z') return false;
            foreach (byte b in word)
                if (b is not ((>= (byte)'a' and <= (byte)'z') or (>= (byte)'0' and <= (byte)'9') or (byte)'-' or (byte)'_'))
                    return false;
            return true;
        }
    }

    /// <summary>What became of one dealer MESSAGE once the host's own handlers had their turn — the capture's call on
    /// whether it was "ignored". 2026-10-01 flight recorder: 828 <c>FrameIgnored</c> in nine days, 428 of them playlist
    /// pushes the library applies in place and 12 collection pushes it re-asks, because the record was written BEFORE the
    /// library looked and said so for every frame. PURE.</summary>
    public static class DealerFrameDisposition
    {
        public enum Verdict : byte
        {
            /// <summary>Something here acted on the frame (the playback or telemetry handlers, or the library host): no record.</summary>
            Handled = 0,
            /// <summary>Nobody acted, and that is the decision (<c>liked-songs-artist</c>): recorded, never an anomaly.</summary>
            IgnoredOnPurpose,
            /// <summary>Nobody acted and nobody decided: a topic this app does not read — the Jam's
            /// <c>social-connect/v2/broadcast_status_update</c> (reading it is declined for now), or one not seen before. Recorded
            /// and an anomaly, so a new topic is visible.</summary>
            Unread,
        }

        /// <param name="handledLocally">The playback-speed or telemetry-progress handler took the frame.</param>
        /// <param name="libraryTook">The library host took it (<c>Library.OnDealerPush</c>'s answer): a playlist push it replays
        /// or marks dirty, a collection or rootlist push it re-asks, a ban, show or saved-episodes push.</param>
        /// <param name="topic">The topic's own kind, which says what a frame nobody took was.</param>
        public static Verdict Of(bool handledLocally, bool libraryTook, DealerTopicRules.Kind topic)
            => handledLocally || libraryTook ? Verdict.Handled
             : topic == DealerTopicRules.Kind.IgnoredOnPurpose ? Verdict.IgnoredOnPurpose
             : Verdict.Unread;

        /// <summary>The <c>FrameIgnored</c> record's reason for a frame that is not <see cref="Verdict.Handled"/>; false (and
        /// no reason) for one that is, which is never recorded.</summary>
        public static bool TryReason(Verdict verdict, out CaptureIgnoreReason reason)
        {
            reason = verdict == Verdict.IgnoredOnPurpose ? CaptureIgnoreReason.IgnoredOnPurpose : CaptureIgnoreReason.Unread;
            return verdict != Verdict.Handled;
        }
    }

    /// <summary>When the keepalive gives up on a socket that stopped answering. librespot waits 3 s for a pong; the old
    /// rule here was "70 s without any frame" evaluated on a 30 s tick — up to ninety blind seconds on a half-open socket.
    /// PURE.</summary>
    public static class DealerLiveness
    {
        /// <summary>The ping cadence (P10 names this timer).</summary>
        public const int PingIntervalMs = 30_000;

        /// <summary>How long after a ping any frame at all must have arrived. Observed pong latency: median 29 ms, p95 73 ms,
        /// one 10 s outlier in nine days — 8 s is generous for a live socket and short for a dead one.</summary>
        public const int PongDeadlineMs = 8_000;

        /// <summary>True when a ping went out at <paramref name="pingSentAtMs"/>, nothing has arrived since
        /// (<paramref name="lastFrameMs"/>) and the deadline has passed.</summary>
        public static bool PongOverdue(long pingSentAtMs, long lastFrameMs, long nowMs)
            => lastFrameMs < pingSentAtMs && nowMs - pingSentAtMs >= PongDeadlineMs;
    }

    /// <summary>How long the dealer waits before its next connect. PURE.</summary>
    public static class ReconnectDelay
    {
        /// <summary>A clean server Close frame gets an immediate first retry (the server is up and asked us to come back;
        /// 3 s of dead air lost commands in the gap), jittered so a fleet restart does not stampede. Every other drop — an
        /// error, a reset, a repeat — climbs <see cref="BackoffMs"/>'s 3/6/12/24/30 s ladder.</summary>
        public const int CleanCloseJitterMs = 500;

        /// <param name="attempt"><see cref="Session.DealerAttempt"/>: 1 for the first retry after a drop.</param>
        /// <param name="jitterSample">Any non-negative random sample; only used for the clean-close jitter.</param>
        public static int For(uint attempt, bool wasCleanClose, int jitterSample = 0)
            => wasCleanClose && attempt <= 1
                ? (int)((uint)jitterSample % (CleanCloseJitterMs + 1))
                : BackoffMs(attempt);
    }

    /// <summary>The ordered hosts apresolve listed for one service, and the one in use. A transport failure against the
    /// current host moves to the next (wrapping); a failure reported for a host that is no longer current moves nothing,
    /// so several threads failing at once advance one step. Thread-safe.</summary>
    public sealed class HostRotation
    {
        string[] _hosts = [];
        int _index;
        readonly Lock _gate = new();

        public static int Next(int index, int count) => count <= 1 ? 0 : (index + 1) % count;

        public void Set(string[] hosts)
        {
            lock (_gate) { _hosts = hosts; _index = 0; }
        }

        /// <summary>The host in use, or null when apresolve has not listed any.</summary>
        public string? Current
        {
            get { lock (_gate) return _hosts.Length == 0 ? null : _hosts[_index]; }
        }

        /// <summary>The caller's connection to <paramref name="failedHost"/> failed at the transport level. True when this
        /// moved to a different host.</summary>
        public bool NoteFailure(string failedHost)
        {
            lock (_gate)
            {
                if (_hosts.Length <= 1 || _hosts[_index] != failedHost) return false;
                _index = Next(_index, _hosts.Length);
                return true;
            }
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
