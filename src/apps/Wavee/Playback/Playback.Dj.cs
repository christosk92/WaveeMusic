// ── Playback/Playback.Dj.cs ──────────────────────────────────────────────────────────────────────────────────────────
// AI DJ (plan-dj D1): a context whose url is a LEXICON SESSION is resolved through the lexicon session provider, not
// context-resolve: the session answer, then its pages of 5-track segments, each the url the answer before it named.
//
// Role: SHELL (the fetches run on an api thread; the session record and the DJ book are the UI thread's)
// Spec: the AI DJ plan (plan-dj) §1.2, §1.3, §2.1, §2.5
//
// A NAMED PARTIAL OF `Playback.Host.cs`, beside the context path it branches from (`Playback.Host.Context.cs`):
//
//   PlayContext(DJ list) ─┐  load.ContextUrl = the list's lexicon_context_url
//   RunLoad(remote play) ─┴▶ ResolveContext ─▶ url is hm://lexicon-session-provider/… ─▶ ResolveLexicon (api thread):
//                              GET <url as an spclient path>&reason=<play reason, default interactive>   the session
//                              GET its next_page_url, verbatim, StartPages times                         segment pages
//                            ─▶ the rows of every page, the last page's next url ─▶ Resolved ─▶ StartContext
//                                                                                     └▶ Dj.Begin: the session record
//   the refill (Playback.Refill.cs: fewer than MinPagesAhead unplayed pages) ─▶ RequestPage ─▶ RequestDjPage:
//                              the pending url (or, past lexicon_expiration_time, the session again and its first page)
//                            ─▶ AppendRows (Context NextUp) ─▶ Paged
//
// The server writes every page url (requestId, previousSegmentId): the client never builds one. Every lexicon GET carries
// `x-dynamic-device-context` (§1.2), and under --fake the answer comes from `Dj.UseFake` instead of the wire, so the same
// decode and the same queue build run headless.
//
// THE DJ BOOK. A lexicon page's rows carry their DJ keys (`narration.intro/jump/outro.*`, `dj.jump_target`, `segment`, the
// decision id, the attribution, the station subtitle, the talk mode, the album images). They are kept per queue row, keyed by
// its item id (the uid book's pattern), and only for DJ rows: the narration's SSML is the seam the narration phase (D2) reads.

using System.Text;
using System.Text.Json;

using ClusterBuffer = Wavee.Spotify.Decode.ClusterBuffer;
using ClusterTrack = Wavee.Spotify.Decode.ClusterTrack;
using RemoteLoad = Wavee.Spotify.Decode.RemoteLoad;

namespace Wavee;

public static partial class Playback
{
    /// <summary>The lexicon session a DJ context plays from (plan-dj §2.1): its fetch, its url rules, the record of what was
    /// fetched and the DJ book of its rows. UI thread, except <see cref="Fetch"/> (an api thread).</summary>
    public static partial class Dj
    {
        /// <summary>A context url served by the lexicon session provider.</summary>
        public const string LexiconPrefix = "hm://lexicon-session-provider/";

        /// <summary>Pages fetched back to back after the session at a start (the official client: session + 3).</summary>
        public const int StartPages = 3;

        /// <summary>The refill rule after the start (plan-dj §1.2): fetch the next page when fewer than this many pages after the
        /// playing one are loaded and unplayed. The trigger at a natural segment boundary is not captured yet (capture C1).</summary>
        public const int MinPagesAhead = 2;

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

        /// <summary>GET one lexicon answer (the session, <paramref name="djPage"/> 0, or page <paramref name="djPage"/>) and decode it
        /// into <paramref name="buffer"/>. Api thread, inside a <see cref="Bodies.Lend"/> scope. False, logged as
        /// <paramref name="what"/>, for a refused or empty answer.</summary>
        internal static bool Fetch(string path, ClusterBuffer buffer, int djPage, string what, out Spotify.Decode.ContextPage page)
        {
            page = default;
            if (path.Length == 0) return false;
            if (s_fake is { } fake)
            {
                byte[]? body = fake(path);
                if (body is null || body.Length == 0) { Log.Warn("dj", what + " refused (fake: no answer)"); return false; }
                return Decode(body, buffer, djPage, what, out page);
            }
            var args = new Spotify.RequestArgs
            {
                Path = path,
                Host = Spotify.ApiHost.Spclient,
                Verb = Spotify.Verb.Get,
                Headers = Spotify.HeaderSet.Bearer | Spotify.HeaderSet.ClientToken | Spotify.HeaderSet.Identity
                          | Spotify.HeaderSet.AcceptLanguage | Spotify.HeaderSet.AcceptJson | Spotify.HeaderSet.DeviceContext
                          | Spotify.HeaderSet.TimeZone | Spotify.HeaderSet.ClientId,   // the official client's pair on these routes (§1.2)
            };
            Spotify.Api.Result result = Spotify.Api.Send(Spotify.RequestKind.Custom, args, CancellationToken.None);
            if (!result.Ok || result.Length == 0) { Log.Warn("dj", what + " refused (" + result.Status + ")"); return false; }
            return Decode(result.Bytes, buffer, djPage, what, out page);
        }

        // ── the session record (UI thread) ──────────────────────────────────────────────────────────────────────────

        /// <summary>One fetched page: its ordinal (1 = the first real page; the session answer is page 0), the url it was GOT
        /// from, how many rows it brought, its own metadata's segment (<c>lastComponentId</c>) and correlation id, and that metadata
        /// whole (the colours among it: what Connect publishes as the page metadata while the page plays, plan-dj §1.8).</summary>
        public sealed record Page(int Ordinal, string Url, int Rows, string Segment, string Correlation, IReadOnlyDictionary<string, string> Metadata)
        {
            /// <summary>Page <paramref name="ordinal"/> as <paramref name="page"/> decoded it into <paramref name="buffer"/>. Api thread.</summary>
            public static Page Of(int ordinal, string url, in Spotify.Decode.ContextPage page, ClusterBuffer buffer)
                => new(ordinal, url, page.TrackCount, Encoding.UTF8.GetString(buffer.Utf8(page.Segment)),
                    Encoding.UTF8.GetString(buffer.Utf8(page.CorrelationId)), MetadataOf(buffer.Utf8(page.Metadata)));
        }

        /// <summary>A metadata object's string entries (a non-string value is skipped). Api thread; only lexicon answers pay for it.</summary>
        static Dictionary<string, string> MetadataOf(ReadOnlySpan<byte> json)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (json.IsEmpty) return map;
            try
            {
                var r = new Utf8JsonReader(json);
                if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return map;
                while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                {
                    string key = r.GetString() ?? "";
                    r.Read();
                    if (r.TokenType == JsonTokenType.String) map[key] = r.GetString() ?? "";
                    else r.Skip();
                }
            }
            catch (JsonException) { /* what was read stands */ }
            return map;
        }

        /// <summary>What the session answer states, built on the api thread: its uri and url, the DJ marker, the expiry, the context's
        /// restrictions (each the first reason, "" = allowed) and the context metadata (the welcome narration's keys among them).</summary>
        public sealed class SessionFacts
        {
            public string Uri = "", Url = "";
            public bool IsDj, Infinite;
            public long ExpiresAt;
            public string NoShuffle = "", NoRepeatContext = "", NoRepeatTrack = "";
            public Dictionary<string, string> Metadata = new(StringComparer.Ordinal);

            /// <summary>Read the session answer <paramref name="page"/> decoded into <paramref name="buffer"/>. Api thread.</summary>
            public static SessionFacts Of(in Spotify.Decode.ContextPage page, ClusterBuffer buffer)
            {
                return new SessionFacts
                {
                    Uri = Encoding.UTF8.GetString(buffer.Utf8(page.Uri)),
                    Url = Encoding.UTF8.GetString(buffer.Utf8(page.Url)),
                    IsDj = page.Dj,
                    Infinite = page.Infinite,
                    ExpiresAt = page.ExpiresAt,
                    NoShuffle = Encoding.UTF8.GetString(buffer.Utf8(page.NoShuffle)),
                    NoRepeatContext = Encoding.UTF8.GetString(buffer.Utf8(page.NoRepeatContext)),
                    NoRepeatTrack = Encoding.UTF8.GetString(buffer.Utf8(page.NoRepeatTrack)),
                    Metadata = MetadataOf(buffer.Utf8(page.Metadata)),
                };
            }
        }

        /// <summary>What a lexicon resolve found, built on the api thread and handed to the start (<see cref="Begin"/>).</summary>
        public sealed class Resolve
        {
            /// <summary>The context url as the play named it (no <c>reason</c>), and the reason it was resolved with.</summary>
            public string ContextUrl = "", Reason = DefaultReason;
            public SessionFacts Facts = new();
            public readonly List<Page> Pages = new(StartPages);
        }

        /// <summary>The session playing now (null: no lexicon context started, or another context replaced it).</summary>
        public sealed class Session
        {
            public EntityId Context;
            public string ContextUrl = "", Reason = DefaultReason;
            public SessionFacts Facts = new();
            public readonly List<Page> Pages = new();
            /// <summary>The next page's url, as the last page named it ("" when none).</summary>
            public string PendingUrl = "";
            /// <summary>How this DJ play began (a start, a transfer into Wavee, the launch restore): the narration phase reads it (a
            /// welcome opens a start; whether a transfer or a resume gets one is not captured).</summary>
            public DjStart Start;
            /// <summary>How many times a page fetch found the session past its expiry and resolved it again.</summary>
            public int Renewals;
            /// <summary>The highest page ordinal loaded (0 before the first page).</summary>
            public int LastPage => Pages.Count == 0 ? 0 : Pages[^1].Ordinal;
            /// <summary>Autoplay never continues this context: it is endless, or it says so (<c>autoplay_candidate=false</c>,
            /// <c>end_of_context_action=stop</c>, plan-dj §1.3).</summary>
            public bool RefusesAutoplay => Facts.Infinite
                || (Facts.Metadata.TryGetValue("autoplay_candidate", out string? candidate) && candidate == "false")
                || (Facts.Metadata.TryGetValue("end_of_context_action", out string? end) && end == "stop");
        }

        static Session? s_session;

        /// <summary>The session record, for the inspector and the paging. Null when no lexicon context plays.</summary>
        public static Session? Current => s_session;

        /// <summary>The session of <paramref name="context"/>, or null.</summary>
        public static Session? Of(EntityId context) => s_session is { } s && s.Context.Equals(context) ? s : null;

        /// <summary>Does the session of <paramref name="context"/> refuse autoplay (<see cref="Session.RefusesAutoplay"/>)? False for any
        /// other context. Allocation-free; the reducer reads it.</summary>
        public static bool RefusesAutoplay(EntityId context) => Of(context) is { RefusesAutoplay: true };

        /// <summary>A lexicon context starts (<see cref="StartContext"/>, after the queue was replaced): the record of the resolve, the
        /// start's pending url. The book keeps the rows the new queue holds.</summary>
        internal static void Begin(EntityId context, Resolve resolve, string pendingUrl, DjStart start = DjStart.Play)
        {
            var s = new Session { Context = context, ContextUrl = resolve.ContextUrl, Reason = resolve.Reason, Facts = resolve.Facts, PendingUrl = pendingUrl,
                Start = start };
            s.Pages.AddRange(resolve.Pages);
            s_session = s;
            Retain(Queue.Rows);
            int rows = 0;
            foreach (Page p in resolve.Pages) rows += p.Rows;
            Log.Info("dj", "lexicon session started: dj=" + (resolve.Facts.IsDj ? 1 : 0) + " pages=" + resolve.Pages.Count + " rows=" + rows
                + " booked=" + s_book.Count + " more=" + (pendingUrl.Length > 0 ? 1 : 0) + " start=" + start);
        }

        /// <summary>A start of another context ends the session (<paramref name="context"/> is the new deck's) and empties the book.</summary>
        internal static void Leave(EntityId context)
        {
            if (s_session is not { } s || s.Context.Equals(context)) return;
            s_session = null;
            s_book.Clear();
            Log.Info("dj", "lexicon session ended: another context started");
        }

        /// <summary>A page landed for the session of <paramref name="context"/>: its record, its next url, and (a renewal) the facts of
        /// the session answer fetched again.</summary>
        internal static void PageLanded(EntityId context, Page? page, string next, SessionFacts? renewed)
        {
            if (Of(context) is not { } s) return;
            if (renewed is not null) { s.Facts = renewed; s.Renewals++; }
            if (page is not null) s.Pages.Add(page);
            s.PendingUrl = next;
            Retain(Queue.Rows);
        }

        /// <summary>How many loaded pages lie after the one the deck at <paramref name="cursor"/> plays from (the deck's page is its
        /// row's, or the nearest DJ row's before it), or -1 when <paramref name="context"/> plays no lexicon session. The refill
        /// rule reads it (<see cref="Refill.Decide"/>). Allocation-free.</summary>
        public static int PagesAhead(EntityId context, ReadOnlySpan<QueueEdge> rows, int cursor)
        {
            if (Of(context) is not { } s) return -1;
            int playing = 0;
            for (int i = Math.Min(cursor, rows.Length - 1); i >= 0; i--)
                if (s_book.TryGetValue(rows[i].ItemId, out Row? row)) { playing = row.Page; break; }
            return Math.Max(0, s.LastPage - playing);
        }

        // ── the DJ book (UI thread) ──────────────────────────────────────────────────────────────────────────────────

        /// <summary>One narration slot of a row (<c>narration.&lt;slot&gt;.*</c>): every key the page stated, by suffix
        /// (<c>ssml</c>, <c>title</c>, <c>voice</c>, <c>tts_provider</c>, <c>loudness</c>, <c>decision_id</c>, …). The narration
        /// phase reads it.</summary>
        public sealed class Narration
        {
            public readonly Dictionary<string, string> Keys = new(StringComparer.Ordinal);
            public string Ssml => Keys.TryGetValue("ssml", out string? ssml) ? ssml : "";
        }

        /// <summary>What a DJ row's page said about it (plan-dj §1.3).</summary>
        public sealed class Row
        {
            /// <summary>The page the row came from: Wavee's own count of the pages fetched for the session the play started (1 = its
            /// first real page; the session answer is page 0). A renewal past <c>lexicon_expiration_time</c> goes on counting
            /// (<see cref="RequestDjPage"/>: the last page + 1), so the Connect index this numbers (plan-dj §1.9 f6) never goes back
            /// while one DJ play lasts; a new DJ start numbers from 1 again.</summary>
            public int Page;
            /// <summary>The row's slot in its page's <c>tracks</c> (0-based): the index's <c>track</c>.</summary>
            public int Track;
            public string Segment = "", DecisionId = "", Attribution = "", Subtitle = "", TalkMode = "", ImageSmall = "", ImageLarge = "";
            /// <summary>Where "Let DJ pick" lands: the segment's first track.</summary>
            public bool JumpTarget;
            public Narration? Intro, Jump, Outro;
            /// <summary>Every DJ key the page stated for the row, in its order (the narration slots' too): what a DJ player publishes on
            /// the row's <c>ProvidedTrack</c> (plan-dj §1.9; the protocol's advice is to echo them all).</summary>
            public KeyValuePair<string, string>[] Keys = [];
        }

        static readonly Dictionary<ulong, Row> s_book = new();

        /// <summary>The DJ row booked under <paramref name="itemId"/>, or null.</summary>
        public static Row? RowOf(ulong itemId) => itemId != 0 && s_book.TryGetValue(itemId, out Row? row) ? row : null;

        /// <summary>How many rows the book holds.</summary>
        public static int Booked => s_book.Count;

        /// <summary>Book a lexicon row (<see cref="ClusterTrack.DjPage"/> &gt; 0) under its queue item id: its DJ keys, out of the
        /// buffer's meta range. Called where a row gets its item id (<c>AddRow</c>, <c>AppendRows</c>).</summary>
        internal static void Note(ulong itemId, ClusterBuffer buffer, in ClusterTrack track)
        {
            if (itemId == 0 || track.DjPage <= 0) return;
            var row = new Row { Page = track.DjPage, Track = track.DjSlot };
            ReadOnlySpan<Spotify.Decode.ClusterMeta> meta = buffer.Meta(track.DjStart, track.DjCount);
            var keys = new KeyValuePair<string, string>[meta.Length];
            int k = 0;
            foreach (ref readonly Spotify.Decode.ClusterMeta m in meta)
            {
                string key = Encoding.UTF8.GetString(buffer.Utf8(m.Key));
                string value = Encoding.UTF8.GetString(buffer.Utf8(m.Value));
                keys[k++] = new(key, value);
                switch (key)
                {
                    case "segment": row.Segment = value; break;
                    case "decision_id": row.DecisionId = value; break;
                    case "custom_reporting_attribution": row.Attribution = value; break;
                    case "station_subtitle": row.Subtitle = value; break;
                    case "automix.talk_mode": row.TalkMode = value; break;
                    case "album_image_small": row.ImageSmall = value; break;
                    case "album_image_large": row.ImageLarge = value; break;
                    case "dj.jump_target": row.JumpTarget = value == "true"; break;
                    default:
                        if (key.StartsWith("narration.", StringComparison.Ordinal))
                        {
                            int dot = key.IndexOf('.', "narration.".Length);
                            if (dot < 0) break;
                            string slot = key["narration.".Length..dot], suffix = key[(dot + 1)..];
                            Narration? n = slot switch
                            {
                                "intro" => row.Intro ??= new Narration(),
                                "jump" => row.Jump ??= new Narration(),
                                "outro" => row.Outro ??= new Narration(),
                                _ => null,
                            };
                            if (n is not null) n.Keys[suffix] = value;
                        }
                        break;
                }
            }
            row.Keys = keys;
            s_book[itemId] = row;
        }

        /// <summary>Forget every booked row no live queue row carries any more.</summary>
        internal static void Retain(ReadOnlySpan<QueueEdge> live)
        {
            if (s_book.Count == 0) return;
            foreach (KeyValuePair<ulong, Row> entry in s_book)
            {
                bool present = false;
                for (int i = 0; i < live.Length && !present; i++) present = live[i].ItemId == entry.Key;
                if (!present) s_book.Remove(entry.Key);
            }
        }

        /// <summary>The inspector's `dj do=expire`: the session's validity ends now, so the next page fetch resolves it again.</summary>
        public static bool ExpireForInspect()
        {
            if (s_session is not { } s) return false;
            s.Facts.ExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1;
            return true;
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
        Log.Info("dj", "lexicon session resolve reason=" + reason + " cause=" + cause);
        bool queued = Spotify.Api.Run(() =>
        {
            int start = buffer.TrackCount, count = 0;
            string pageUrl = "";
            Dj.Resolve? resolve = null;
            try
            {
                using Bodies.LendScope lent = Bodies.Lend();   // decoded into the cluster buffer, then dropped (Platform/Bodies.cs)
                resolve = FetchSession(path, url, reason, buffer, out TextRef metadata, out pageUrl);
                if (resolve is not null)
                {
                    load.ResolvedMetadata = metadata;
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

    /// <summary>The session answer at <paramref name="path"/> and its first <see cref="Dj.StartPages"/> pages, decoded into
    /// <paramref name="buffer"/>: what a start (<see cref="ResolveLexicon"/>) and a launch restore (<see cref="ResolveDjSeed"/>) fetch.
    /// Null when the session answer is refused. Api thread, inside a <see cref="Bodies.Lend"/> scope.</summary>
    static Dj.Resolve? FetchSession(string path, string url, string reason, ClusterBuffer buffer, out TextRef metadata, out string next)
    {
        metadata = default;
        next = "";
        if (!Dj.Fetch(path, buffer, 0, "dj session", out Spotify.Decode.ContextPage session)) return null;
        var resolve = new Dj.Resolve { ContextUrl = url, Reason = reason, Facts = Dj.SessionFacts.Of(in session, buffer) };
        metadata = session.Metadata;
        next = Encoding.UTF8.GetString(buffer.Utf8(session.NextPageUrl));
        for (int ordinal = 1; ordinal <= Dj.StartPages && next.Length > 0; ordinal++)
        {
            if (!Dj.Fetch(RemotePlan.PageRoute(next), buffer, ordinal, "dj page " + ordinal, out Spotify.Decode.ContextPage page)) break;
            resolve.Pages.Add(Dj.Page.Of(ordinal, next, in page, buffer));
            next = Encoding.UTF8.GetString(buffer.Utf8(page.NextPageUrl));
        }
        return resolve;
    }

    /// <summary>The DJ refill (<see cref="RequestPage"/> for a lexicon context): the pending page url, or — past the session's
    /// <c>lexicon_expiration_time</c> — the session again and its first page (play goes on from that page, plan-dj §2.1.5). The
    /// rows append as Context NextUp and book their DJ keys; <see cref="Input.Paged"/> answers. UI thread.</summary>
    static void RequestDjPage(EntityId context, Dj.Session session)
    {
        long now = FrameNowMs();
        long seq = s_contextSeq;
        int ordinal = session.LastPage + 1;
        bool expired = session.Facts.ExpiresAt > 0 && DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= session.Facts.ExpiresAt;
        string pending = s_pageUrl, contextUrl = session.ContextUrl, reason = session.Reason;
        Spotify.Api.DeviceContext = Dj.DeviceContext(OutputDeviceName());
        Log.Info("dj", "lexicon page " + ordinal + (expired ? ": the session expired, resolving it again" : ": fetching the pending page"));
        bool queued = Spotify.Api.Run(() =>
        {
            ClusterBuffer buffer = ClusterBuffer.Rent();
            int start = buffer.TrackCount, count = 0;
            string next = "", url = pending;
            Dj.Page? page = null;
            Dj.SessionFacts? renewed = null;
            try
            {
                using Bodies.LendScope lent = Bodies.Lend();
                if (expired)
                {
                    url = "";
                    if (Dj.Fetch(Dj.SessionRoute(contextUrl, reason), buffer, 0, "dj session (renewal)", out Spotify.Decode.ContextPage s))
                    {
                        renewed = Dj.SessionFacts.Of(in s, buffer);
                        url = Encoding.UTF8.GetString(buffer.Utf8(s.NextPageUrl));
                    }
                    start = buffer.TrackCount;
                }
                if (url.Length > 0 && Dj.Fetch(RemotePlan.PageRoute(url), buffer, ordinal, "dj page " + ordinal, out Spotify.Decode.ContextPage p))
                {
                    count = buffer.TrackCount - start;
                    next = Encoding.UTF8.GetString(buffer.Utf8(p.NextPageUrl));
                    page = Dj.Page.Of(ordinal, url, in p, buffer);
                }
            }
            catch (Exception ex) { Log.Warn("dj", "page " + ordinal + " failed", ex); }
            ToUi(() =>
            {
                try
                {
                    bool current = seq == s_contextSeq;
                    int appended = current ? AppendRows(context, buffer, start, count, QueueProvider.Context) : 0;
                    if (current && context.Equals(s_pageContext)) s_pageUrl = appended > 0 ? next : "";
                    if (current) Dj.PageLanded(context, appended > 0 ? page : null, appended > 0 ? next : "", renewed);
                    Log.Info("dj", "lexicon page " + ordinal + " appended " + appended + " row(s), " + (next.Length > 0 ? "more follow" : "the last"));
                    Post(Input.Paged(context, appended, morePages: appended > 0 && next.Length > 0, FrameNowMs()));
                }
                finally { ClusterBuffer.Return(buffer); }
            });
        });
        if (!queued) Post(Input.Paged(context, 0, morePages: false, now));
    }
}
