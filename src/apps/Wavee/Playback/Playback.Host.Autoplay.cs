// ── Playback/Playback.Host.Autoplay.cs ───────────────────────────────────────────────────────────────────────────────
// Autoplay: the first ask, its pages, the rows appended, and the session watch that re-issues an ask made offline.
//
// Role: SHELL
// Owner: G
// Wave: queue refill (2026-09-16); RequestAutoplay / AppendRows / AutoplaySeeds moved here from Playback.Host.Context.cs
// Budget: 300 lines
// Spec: gap register G-080, G-241; docs/plans/wavee/queue-refill-and-visual-defects-2026-09-16-implementation.md §3 WS-B
//
// A NAMED PARTIAL OF `Playback.Host.cs`, the same SHELL rules: the UI thread owns the queue (C1), blocking work runs on
// an api thread (`Spotify.Api.Run`) and comes back through `ToUi` / `Post` (C9).
//
//   fx.Autoplay     ─▶ RequestAutoplay(ctx)     POST /context-resolve/v1/autoplay ─▶ AppendRows(Autoplay) ─▶ Autoplayed
//                                               offline / 401 / 403 ─▶ Autoplayed(retry) ─▶ AutoplayPhase.Deferred
//   fx.AutoplayPage ─▶ RequestAutoplayPage(ctx) GET the answer's next_page_url ─▶ AppendRows(Autoplay) ─▶ AutoplayPaged
//   Spotify.Status → Online                    ─▶ Input.SessionOnline (a permanent watch, installed at Boot)

using System.Buffers;

using FluentGpu.Foundation;
using FluentGpu.Signals;

using ClusterBuffer = Wavee.Spotify.Decode.ClusterBuffer;
using ClusterTrack = Wavee.Spotify.Decode.ClusterTrack;

namespace Wavee;

public static partial class Playback
{
    // ── 1. the ask ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>How many already-played rows seed an autoplay request (newest first).</summary>
    const int AutoplaySeeds = 20;

    /// <summary>The next page of the autoplay answer, as the answer named it; "" when it had none.</summary>
    static string s_autoplayPageUrl = "";
    static EntityId s_autoplayPageContext;

    /// <summary>Ask <c>/context-resolve/v1/autoplay</c> for more and append what comes back as
    /// <see cref="QueueProvider.Autoplay"/> NextUp. Declined — answered with zero rows — when the setting is off or the
    /// context is one autoplay cannot continue. <c>AutoplayContextRequest.context_uri</c> (field 1) takes the context's
    /// own uri VERBATIM — including a bare <c>spotify:track:</c>/<c>spotify:episode:</c> "context" from a card: the server
    /// resolves the station itself, the same way <c>/context-resolve/v1/&lt;uri&gt;</c> already accepts a station uri
    /// (<see cref="ResolveContext"/>). A 4xx on a bare track/episode falls back to resolving its station
    /// (<see cref="RemotePlan.StationUri"/>) through that same context-resolve path — the precedent
    /// <see cref="ResolveSeedContext"/>/<see cref="LandSeedContext"/> uses for "Start radio" — so a server that refuses
    /// the bare uri still gets an answer instead of the rail staying empty.
    /// <para>Offline, or refused by the session (401/403), the answer is a RETRY: the reducer defers the ask and the
    /// session watch (<see cref="WatchSession"/>) has it asked again once online.</para></summary>
    static void RequestAutoplay(EntityId context)
    {
        long now = FrameNowMs();
        if (!Platform.Settings.Get(Platform.Keys.AutoplayEnabled) || context.IsEmpty
            || context.Provider != EntityProvider.Spotify || Entities.Current is null)
        {
            Post(Input.Autoplayed(context, 0, nowMs: now));
            return;
        }
        Span<byte> text = stackalloc byte[256];
        int length = context.Format(text);
        if (!RemotePlan.AutoplayContinues(text[..length])) { Post(Input.Autoplayed(context, 0, nowMs: now)); return; }
        if (!Spotify.Current.IsOnline)
        {
            Log.Info("playback", "autoplay deferred: the session is not online yet");
            Post(Input.Autoplayed(context, 0, morePages: false, retry: true, nowMs: now));
            return;
        }

        byte[] contextUtf8 = text[..length].ToArray();
        int last = Math.Min(s_state.Cursor.Index, Queue.Count - 1);
        int seeds = Math.Clamp(last + 1, 0, AutoplaySeeds);
        var recent = new EntityId[seeds];
        for (int k = 0; k < seeds; k++) recent[k] = Queue.RefAt(last - k).Id;
        bool podcast = context.Kind is EntityKind.Show or EntityKind.Episode;
        if (podcast)
        {
            EntityId[] history = Spotify.Telemetry.RecentEpisodes();
            var unique = new HashSet<EntityId>();
            var all = new List<EntityId>();
            if (s_state.CurrentId.Kind == EntityKind.Episode && unique.Add(s_state.CurrentId)) all.Add(s_state.CurrentId);
            foreach (EntityId id in history) if (unique.Add(id)) all.Add(id);
            foreach (EntityId id in recent) if (id.Kind == EntityKind.Episode && unique.Add(id)) all.Add(id);
            recent = all.ToArray();
        }
        string station = RemotePlan.StationUri(context);      // "" unless context is a bare track/artist seed

        bool queued = Spotify.Api.Run(() =>
        {
            ClusterBuffer buffer = ClusterBuffer.Rent();
            int start = 0, count = 0;
            string next = "";
            bool retry = false;
            try
            {
                byte[] body = new byte[64 + 64 * recent.Length];
                int written = podcast ? Spotify.Decode.AutopodcastRequest(recent, body) : Spotify.Decode.AutoplayRequest(contextUtf8, recent, body);
                Spotify.Api.Result result = Spotify.Api.Autoplay(body.AsSpan(0, written).ToArray(), podcast, CancellationToken.None);
                if (result.Ok && result.Body.Length > 0)
                {
                    Spotify.Decode.ContextPage page = Spotify.Decode.ContextResolve(result.Bytes, buffer);
                    start = page.TrackStart;
                    count = page.TrackCount;
                    if (!page.NextPageUrl.IsEmpty) next = System.Text.Encoding.UTF8.GetString(buffer.Utf8(page.NextPageUrl));
                }
                else
                {
                    Log.Warn("playback", "autoplay refused (" + result.Status + ")");
                    retry = result.Status is 401 or 403;
                    if (result.Status is >= 400 and < 500 && station.Length > 0)
                    {
                        Spotify.Api.Result fallback = Spotify.Api.ContextResolve(station, CancellationToken.None);
                        if (fallback.Ok && fallback.Body.Length > 0)
                        {
                            Spotify.Decode.ContextPage page = Spotify.Decode.ContextResolve(fallback.Bytes, buffer);
                            start = page.TrackStart;
                            count = page.TrackCount;
                            if (!page.NextPageUrl.IsEmpty) next = System.Text.Encoding.UTF8.GetString(buffer.Utf8(page.NextPageUrl));
                        }
                        else Log.Warn("playback", "autoplay station fallback refused (" + fallback.Status + ")");
                    }
                }
            }
            catch (Exception ex) { Log.Warn("playback", "autoplay failed", ex); }
            ToUi(() =>
            {
                try
                {
                    int appended = AppendRows(context, buffer, start, count, QueueProvider.Autoplay, recent);
                    if (context.Equals(s_state.Context))
                    {
                        s_autoplayPageContext = context;
                        s_autoplayPageUrl = appended > 0 ? next : "";
                    }
                    Log.Info("playback", "autoplay appended " + appended + " row(s), " + (next.Length > 0 ? "more follow" : "the last"));
                    Post(Input.Autoplayed(context, appended, morePages: appended > 0 && next.Length > 0,
                        retry: appended == 0 && retry, nowMs: FrameNowMs()));
                }
                finally { ClusterBuffer.Return(buffer); }
            });
        });
        if (!queued) Post(Input.Autoplayed(context, 0, nowMs: now));
    }

    // ── 2. the answer's pages ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The autoplay run is nearly consumed and its answer named a next page: GET it, append its rows as
    /// Autoplay NextUp, keep ITS next url, and answer with <see cref="Input.AutoplayPaged"/>. No page held for this
    /// context answers empty, and the reducer asks autoplay afresh.</summary>
    static void RequestAutoplayPage(EntityId context)
    {
        long now = FrameNowMs();
        string path = context.Equals(s_autoplayPageContext) ? RemotePlan.PageRoute(s_autoplayPageUrl) : "";
        if (path.Length == 0 || Entities.Current is null) { Post(Input.AutoplayPaged(context, 0, morePages: false, now)); return; }
        long seq = s_contextSeq;
        bool queued = Spotify.Api.Run(() =>
        {
            ClusterBuffer buffer = ClusterBuffer.Rent();
            FetchPage(path, buffer, "autoplay page", out int start, out int count, out string next);
            ToUi(() =>
            {
                try
                {
                    int appended = seq == s_contextSeq ? AppendRows(context, buffer, start, count, QueueProvider.Autoplay) : 0;
                    if (seq == s_contextSeq && context.Equals(s_autoplayPageContext)) s_autoplayPageUrl = appended > 0 ? next : "";
                    Log.Info("playback", "autoplay page appended " + appended + " row(s), " + (next.Length > 0 ? "more follow" : "the last"));
                    Post(Input.AutoplayPaged(context, appended, morePages: appended > 0 && next.Length > 0, FrameNowMs()));
                }
                finally { ClusterBuffer.Return(buffer); }
            });
        });
        if (!queued) Post(Input.AutoplayPaged(context, 0, morePages: false, now));
    }

    /// <summary>Drop the held autoplay page: a new context, a new answer, or a test reset.</summary>
    static void ForgetAutoplayPage()
    {
        s_autoplayPageUrl = "";
        s_autoplayPageContext = default;
    }

    // ── 3. the append ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Append a resolve's rows after the whole queue as NextUp of <paramref name="provider"/> — while the context
    /// is still the one on the deck. Answers how many landed.</summary>
    static int AppendRows(EntityId context, ClusterBuffer buffer, int start, int count, QueueProvider provider, EntityId[]? excluded = null)
    {
        if (count <= 0 || !context.Equals(s_state.Context) || Entities.Current is null) return 0;
        Rebind();                                          // append to this scope's queue, never under a stale one (G-241)
        int existing = Queue.Count;
        int cap = existing + Math.Min(count, RemotePlan.MaxRows);
        int[] packed = ArrayPool<int>.Shared.Rent(cap);
        QueueEdge[] rows = ArrayPool<QueueEdge>.Shared.Rent(cap);
        try
        {
            Queue.PackedRefs.CopyTo(packed);
            Queue.Rows.CopyTo(rows);
            int n = existing;
            ReadOnlySpan<ClusterTrack> tracks = buffer.Tracks(start, count);
            for (int k = 0; k < tracks.Length && n < cap; k++)
            {
                ReadOnlySpan<byte> uri = buffer.Utf8(tracks[k].Uri);
                if (uri.IsEmpty) continue;
                EntityId id = EntityId.Parse(uri);
                if (!id.IsPlayable || Spotify.Library.IsBanned(id)) continue;
                if (!tracks[k].ArtistUri.IsEmpty && Spotify.Library.IsBanned(id.Text,
                    [System.Text.Encoding.UTF8.GetString(buffer.Utf8(tracks[k].ArtistUri))])) continue;
                if (provider == QueueProvider.Autoplay)
                {
                    bool duplicate = id.Equals(s_state.CurrentId) || (excluded is not null && Array.IndexOf(excluded, id) >= 0);
                    for (int j = 0; j < n && !duplicate; j++) duplicate = Queue.Unpack(packed[j]).Id.Equals(id);
                    if (duplicate) continue;
                }
                int target = Queue.Pack(Entities.Ref(id));
                if (target == 0) continue;
                packed[n] = target;
                rows[n] = new QueueEdge(s_uids.ItemIdOf(buffer.Utf8(tracks[k].Uid)), (byte)provider, (byte)QueueBucket.NextUp);
                n++;
            }
            int appended = n - existing;
            if (appended > 0)
            {
                FillQueueIds(context, packed.AsSpan(0, n), rows.AsSpan(0, n));
                Queue.Replace(packed.AsSpan(0, n), rows.AsSpan(0, n));
            }
            s_uids.Retain(Queue.Rows);
            return appended;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(packed);
            ArrayPool<QueueEdge>.Shared.Return(rows);
        }
    }

    // ── 4. the session watch ──────────────────────────────────────────────────────────────────────────────────────
    //
    // The same idiom as the seed retry's `WatchSeedOnline` (Playback.Host.Context.cs): a private `ReactiveRuntime` with one
    // `Effect` tracking `Spotify.Status`, flushed through `ToUi`. Unlike the seed's it is never disposed: every transition
    // into Online posts `Input.SessionOnline`, and the reducer decides whether anything was deferred.

    static ReactiveRuntime? s_onlineRuntime;
    static Effect? s_onlineWatch;
    static bool s_onlineFlushPosted;
    static bool s_wasOnline;
    static readonly Action s_onlineFlush = static () => { s_onlineFlushPosted = false; s_onlineRuntime?.Flush(); };

    static void RequestOnlineFlush()
    {
        if (s_onlineFlushPosted) return;
        s_onlineFlushPosted = true;
        ToUi(s_onlineFlush);
    }

    /// <summary>Install the permanent <see cref="Spotify.Status"/> watch. Called once at <see cref="Boot"/>; idempotent.</summary>
    static void WatchSession()
    {
        if (s_onlineRuntime is not null) return;
        s_wasOnline = Spotify.Status.Peek() == Spotify.SessionPhase.Online;
        s_onlineRuntime = new ReactiveRuntime { FrameRequested = RequestOnlineFlush };
        s_onlineWatch = new Effect(s_onlineRuntime, WatchOnline);
    }

    /// <summary>Tracked: re-runs whenever <see cref="Spotify.Status"/> moves; posts on each edge into Online.</summary>
    static void WatchOnline()
    {
        bool online = Spotify.Status.Value == Spotify.SessionPhase.Online;
        if (online && !s_wasOnline) Post(Input.SessionOnline(FrameNowMs()));
        s_wasOnline = online;
    }
}
