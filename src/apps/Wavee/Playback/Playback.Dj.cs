// ── Playback/Playback.Dj.cs ──────────────────────────────────────────────────────────────────────────────────────────
// AI DJ (plan-dj D1): a context whose url is a LEXICON SESSION is resolved through the lexicon session provider, not
// context-resolve: the session answer, then its first pages, each the url the answer before it named.
//
// Role: SHELL (the fetches run on an api thread; the session record is the UI thread's)
// Spec: C:\WAVEE\scratch\apsniff\plans\plan-dj.md §1.2, §2.1 (owner's plan, not in the repo)
//
// A NAMED PARTIAL OF `Playback.Host.cs`, beside the context path it branches from (`Playback.Host.Context.cs`):
//
//   PlayContext(DJ list) ─┐  load.ContextUrl = the list's lexicon_context_url
//   RunLoad(remote play) ─┴▶ ResolveContext ─▶ url is hm://lexicon-session-provider/… ─▶ ResolveLexicon (api thread):
//                              GET <url as an spclient path>&reason=<play reason, default interactive>   the session
//                              GET its next_page_url, verbatim, StartPages times                         segment pages
//                            ─▶ the rows of every page, the last page's next url ─▶ Resolved ─▶ StartContext
//                                                                                     └▶ Dj.Begin: the session record
//
// The server writes every page url (requestId, previousSegmentId): the client never builds one. Every lexicon GET carries
// `x-dynamic-device-context` (§1.2), and under --fake the answer comes from `UseFakeLexicon` instead of the wire, so the
// same decode and the same queue build run headless.

using System.Text;

using ClusterBuffer = Wavee.Spotify.Decode.ClusterBuffer;
using RemoteLoad = Wavee.Spotify.Decode.RemoteLoad;

namespace Wavee;

public static partial class Playback
{
    /// <summary>The lexicon session a DJ context plays from (plan-dj §2.1): its fetch, its url rules and the record of what was
    /// fetched. UI thread, except the fetch itself (an api thread).</summary>
    public static class Dj
    {
        /// <summary>A context url served by the lexicon session provider.</summary>
        public const string LexiconPrefix = "hm://lexicon-session-provider/";

        /// <summary>Pages fetched back to back after the session at a start (the official client: session + 3, L2327930ff).</summary>
        public const int StartPages = 3;

        /// <summary>The play reason a lexicon url is resolved with when the play stated none (a local click).</summary>
        public const string DefaultReason = "interactive";

        /// <summary>Is <paramref name="url"/> a lexicon session url?</summary>
        public static bool IsLexicon(ReadOnlySpan<byte> url) => url.StartsWith("hm://lexicon-session-provider/"u8);

        /// <inheritdoc cref="IsLexicon(ReadOnlySpan{byte})"/>
        public static bool IsLexicon(string url) => url.StartsWith(LexiconPrefix, StringComparison.Ordinal);

        /// <summary>The spclient path a session url is GOT on: the url's own path with <c>reason=</c> appended, unless it already
        /// carries one (a controller may send the url as resolved). PURE.</summary>
        public static string SessionRoute(string url, string reason)
        {
            string path = RemotePlan.PageRoute(url);
            if (path.Length == 0 || path.Contains("reason=", StringComparison.Ordinal)) return path;
            return path + (path.Contains('?') ? "&reason=" : "?reason=") + (reason.Length > 0 ? reason : DefaultReason);
        }

        /// <summary>The <c>x-dynamic-device-context</c> value (plan-dj §1.2): base64 of
        /// <c>{1 {1 "spotify", 2 &lt;model&gt;, 3 "computer", 4 {1 &lt;output name&gt;, 4 3, 5 0}}}</c>, the model the one this
        /// device publishes to Connect (<c>PutState</c>'s device_info 15). Without an output name field 4 is left out, as the
        /// official client sent it on its first page. PURE.</summary>
        public static string DeviceContext(string outputName)
        {
            Span<byte> buffer = stackalloc byte[512];
            var w = new Spotify.Decode.ProtoWriter(buffer);
            int outer = w.Open(1);
            w.Str(1, "spotify");
            w.Str(2, "PC laptop");
            w.Str(3, "computer");
            if (outputName.Length is > 0 and <= 256)
            {
                int output = w.Open(4);
                w.Str(1, outputName);
                w.UAlways(4, 3);
                w.UAlways(5, 0);
                w.Close(output);
            }
            w.Close(outer);
            return Convert.ToBase64String(buffer[..w.Length]);
        }

        // ── the --fake answer ───────────────────────────────────────────────────────────────────────────────────────

        static Func<string, byte[]?>? s_fake;

        /// <summary><c>--fake</c> only (App.cs, beside <c>UseFakeAutoplay</c>): the demo answers a lexicon GET with the BODY the
        /// service would send (<c>Entities.FakeLexicon</c>), so the decode, the paging and the queue build are the real ones. Null:
        /// the network answers.</summary>
        public static void UseFake(Func<string, byte[]?>? answer) => s_fake = answer;

        /// <summary>GET one lexicon answer (the session or a page) and decode it into <paramref name="buffer"/>. Api thread, inside
        /// a <see cref="Bodies.Lend"/> scope. False, logged as <paramref name="what"/>, for a refused or empty answer.</summary>
        internal static bool Fetch(string path, ClusterBuffer buffer, string what, out Spotify.Decode.ContextPage page)
        {
            page = default;
            if (path.Length == 0) return false;
            if (s_fake is { } fake)
            {
                byte[]? body = fake(path);
                if (body is null || body.Length == 0) { Log.Warn("dj", what + " refused (fake: no answer)"); return false; }
                page = Spotify.Decode.ContextResolve(body, buffer);
                return true;
            }
            var args = new Spotify.RequestArgs
            {
                Path = path,
                Host = Spotify.ApiHost.Spclient,
                Verb = Spotify.Verb.Get,
                Headers = Spotify.HeaderSet.Bearer | Spotify.HeaderSet.ClientToken | Spotify.HeaderSet.Identity
                          | Spotify.HeaderSet.AcceptLanguage | Spotify.HeaderSet.AcceptJson | Spotify.HeaderSet.DeviceContext,
            };
            Spotify.Api.Result result = Spotify.Api.Send(Spotify.RequestKind.Custom, args, CancellationToken.None);
            if (!result.Ok || result.Length == 0) { Log.Warn("dj", what + " refused (" + result.Status + ")"); return false; }
            page = Spotify.Decode.ContextResolve(result.Bytes, buffer);
            return true;
        }

        // ── the session record (UI thread) ──────────────────────────────────────────────────────────────────────────

        /// <summary>One fetched page: its ordinal (1 = the first real page; the session answer is page 0), the url it was GOT
        /// from and how many rows it brought.</summary>
        public sealed record Page(int Ordinal, string Url, int Rows);

        /// <summary>What a lexicon resolve found, built on the api thread and handed to the start (<see cref="Begin"/>).</summary>
        public sealed class Resolve
        {
            /// <summary>The context url as the play named it (no <c>reason</c>), the session's own uri and url as it answered them.</summary>
            public string ContextUrl = "", Uri = "", Url = "";
            public readonly List<Page> Pages = new(StartPages);
        }

        /// <summary>The session playing now, or null (no lexicon context started this session, or another context replaced it).</summary>
        public sealed class Session
        {
            public EntityId Context;
            public string ContextUrl = "", Uri = "", Url = "";
            public readonly List<Page> Pages = new();
            /// <summary>The next page's url, as the last page named it ("" when none).</summary>
            public string PendingUrl = "";
        }

        static Session? s_session;

        /// <summary>The session record, for the inspector and the paging. Null when no lexicon context plays.</summary>
        public static Session? Current => s_session;

        /// <summary>The session of <paramref name="context"/>, or null.</summary>
        public static Session? Of(EntityId context) => s_session is { } s && s.Context.Equals(context) ? s : null;

        /// <summary>A lexicon context starts (<see cref="StartContext"/>): the record of the resolve, the start's pending url.</summary>
        internal static void Begin(EntityId context, Resolve resolve, string pendingUrl)
        {
            var s = new Session { Context = context, ContextUrl = resolve.ContextUrl, Uri = resolve.Uri, Url = resolve.Url, PendingUrl = pendingUrl };
            s.Pages.AddRange(resolve.Pages);
            s_session = s;
            int rows = 0;
            foreach (Page p in resolve.Pages) rows += p.Rows;
            Log.Info("dj", "session started: pages=" + resolve.Pages.Count + " rows=" + rows + " more=" + (pendingUrl.Length > 0 ? 1 : 0));
        }

        /// <summary>A start of another context ends the session (<paramref name="context"/> is the new deck's).</summary>
        internal static void Leave(EntityId context)
        {
            if (s_session is not { } s || s.Context.Equals(context)) return;
            s_session = null;
            Log.Info("dj", "session ended: another context started");
        }
    }

    /// <summary>Resolve a LEXICON context (<see cref="Dj"/>): the session, then its first <see cref="Dj.StartPages"/> pages, on an api
    /// thread into <paramref name="buffer"/>, then the one start path (<see cref="Resolved"/>). Called by <see cref="ResolveContext"/>
    /// for a load whose url is a lexicon session url. UI thread.</summary>
    static void ResolveLexicon(long seq, string url, RemoteLoad load, ClusterBuffer buffer, ClaimCause cause)
    {
        string reason = load.Reason.IsEmpty ? Dj.DefaultReason : Encoding.UTF8.GetString(buffer.Utf8(load.Reason));
        string path = Dj.SessionRoute(url, reason);
        Spotify.Api.DeviceContext = Dj.DeviceContext(OutputDeviceName());
        Log.Info("dj", "resolve via lexicon session reason=" + reason + " cause=" + cause);
        bool queued = Spotify.Api.Run(() =>
        {
            int start = buffer.TrackCount, count = 0;
            string pageUrl = "";
            Dj.Resolve? resolve = null;
            try
            {
                using Bodies.LendScope lent = Bodies.Lend();   // decoded into the cluster buffer, then dropped (Platform/Bodies.cs)
                if (Dj.Fetch(path, buffer, "dj session", out Spotify.Decode.ContextPage session))
                {
                    resolve = new Dj.Resolve
                    {
                        ContextUrl = url,
                        Uri = Encoding.UTF8.GetString(buffer.Utf8(session.Uri)),
                        Url = Encoding.UTF8.GetString(buffer.Utf8(session.Url)),
                    };
                    load.ResolvedMetadata = session.Metadata;
                    string next = Encoding.UTF8.GetString(buffer.Utf8(session.NextPageUrl));
                    for (int ordinal = 1; ordinal <= Dj.StartPages && next.Length > 0; ordinal++)
                    {
                        if (!Dj.Fetch(RemotePlan.PageRoute(next), buffer, "dj page " + ordinal, out Spotify.Decode.ContextPage page)) break;
                        resolve.Pages.Add(new Dj.Page(ordinal, next, page.TrackCount));
                        next = Encoding.UTF8.GetString(buffer.Utf8(page.NextPageUrl));
                    }
                    pageUrl = next;
                    count = buffer.TrackCount - start;
                }
            }
            catch (Exception ex) { Log.Warn("dj", "lexicon resolve failed", ex); }
            ToUi(() => Resolved(seq, in load, buffer, start, count, cause, pageUrl, resolve));
        });
        if (queued) return;
        Log.Warn("dj", "lexicon resolve refused: the api queue is full");
        ClusterBuffer.Return(buffer);
        ReleaseHold(seq);
    }
}
