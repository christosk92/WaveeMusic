// ── Screens/Diagnostics.Inspect.Dj.cs ─────────────────────────────────────────────────────────────────────────────────
// the `dj` verb (AI DJ, plan-dj §2.9): what Wavee holds of a DJ list and of the lexicon session playing it.
//
//   dj                    the DJ list (its attributes as the playlist row holds them), the lexicon session playing (its uri,
//                         url, the pages fetched, the next page's url), and the deck and queue as `transport` reads them
//   dj do=answers         the --fake lexicon service's session and first pages, decoded by the real context-resolve decoder
//   dj do=play            start the DJ list as a click does (Playback.PlayContext): waits for the resolve and the load
//
// Role: SHELL (UI thread; reads the entity tables and the fake answers)

using System.Text.Json;

namespace Wavee;

public static partial class Diagnostics
{
    public sealed partial class InspectSession
    {
        /// <summary>`dj {do?, uri?, pages?}`.</summary>
        string DjVerb(int id, string name, Inspect.Args a)
        {
            string verb = a.Str("do").ToLowerInvariant();
            string uri = a.Str("uri", Entities.LibrarySeed.DjUri);
            switch (verb)
            {
                case "":
                case "status":
                    return Inspect.Reply.Ok(id, name, w => { WriteDjList(w, uri); WriteDjSession(w); });
                case "play":
                    {
                        if (_shell is null) return Inspect.Reply.Fail(id, name, "dj do=play needs a mount");
                        if (!EntityId.TryParse(uri.AsSpan(), out EntityId list)) return Inspect.Reply.Fail(id, name, "not a uri: " + uri);
                        int served = Entities.FakeLexiconServed;
                        Playback.PlayContext(list);
                        string phase = "traced";
                        if (!a.Has("trace"))
                        {
                            // The resolve runs on an api thread: frames until the deck names the list (or the budget runs out), then its load.
                            long until = Environment.TickCount64 + PlayLoadBudgetMs;
                            while (!Playback.Snap().Context.Equals(list) && Environment.TickCount64 < until) { RunFrames(1); Thread.Sleep(2); }
                            phase = AwaitPlaybackLoad(PlayLoadBudgetMs);
                        }
                        return Settled(id, name, a, w =>
                        {
                            w.WriteString("do", verb);
                            w.WriteString("phase", phase);
                            w.WriteNumber("lexiconAnswers", Entities.FakeLexiconServed - served);
                            WriteDjSession(w);
                        });
                    }
                case "answers":
                    {
                        int pages = Math.Clamp(a.Int("pages", 3), 0, 12);
                        return Inspect.Reply.Ok(id, name, w =>
                        {
                            WriteDjList(w, uri);
                            WriteFakeAnswers(w, pages);
                        });
                    }
                default:
                    return Inspect.Reply.Fail(id, name, "dj wants do=status|answers|play, not '" + verb + "'");
            }
        }

        /// <summary>The DJ list's row: its title and the two attributes a DJ start reads.</summary>
        static void WriteDjList(Utf8JsonWriter w, string uri)
        {
            w.WriteStartObject("list");
            w.WriteString("uri", uri);
            var eid = EntityId.Parse(uri.AsSpan());
            if (eid.Kind == EntityKind.Playlist && Entities.Current.Playlists.TryGetSlot(eid, out int slot))
            {
                var p = new Playlist(slot);
                w.WriteString("title", Entities.Strings.Resolve(p.TitleId));
                w.WriteBoolean("yourDj", p.IsDj);
                w.WriteString("lexiconUrl", Entities.Strings.Resolve(p.LexiconUrlId));
            }
            else w.WriteBoolean("known", false);
            w.WriteEndObject();
        }

        /// <summary>The lexicon session playing now (none: <c>session: null</c>), then the deck and the queue as <c>transport</c> reads them.</summary>
        static void WriteDjSession(Utf8JsonWriter w)
        {
            if (Playback.Dj.Current is not { } s) w.WriteNull("session");
            else
            {
                w.WriteStartObject("session");
                w.WriteString("context", s.Context.Text);
                w.WriteString("contextUrl", s.ContextUrl);
                w.WriteString("uri", s.Uri);
                w.WriteString("url", s.Url);
                w.WriteNumber("pagesFetched", s.Pages.Count);
                w.WriteStartArray("pages");
                foreach (Playback.Dj.Page p in s.Pages)
                {
                    w.WriteStartObject();
                    w.WriteNumber("page", p.Ordinal);
                    w.WriteNumber("rows", p.Rows);
                    w.WriteString("url", p.Url);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteString("pendingUrl", s.PendingUrl);
                w.WriteEndObject();
            }
            w.WriteString("deviceContext", Spotify.Api.DeviceContext);
            WriteTransport(w);
        }

        /// <summary>The fake service's session and its first <paramref name="pages"/> pages, each through
        /// <see cref="Spotify.Decode.ContextResolve"/>: the shape the resolver will read.</summary>
        static void WriteFakeAnswers(Utf8JsonWriter w, int pages)
        {
            Spotify.Decode.ClusterBuffer buffer = Spotify.Decode.ClusterBuffer.Rent();
            try
            {
                string path = Playback.RemotePlan.PageRoute(Entities.LibrarySeed.DjLexiconUrl) + "&reason=interactive";
                w.WriteStartArray("answers");
                for (int n = 0; n <= pages && path.Length > 0; n++)
                {
                    byte[]? body = Entities.FakeLexicon(path);
                    w.WriteStartObject();
                    w.WriteNumber("page", n);
                    w.WriteString("path", path);
                    if (body is null) { w.WriteBoolean("served", false); w.WriteEndObject(); break; }
                    Spotify.Decode.ContextPage page = Spotify.Decode.ContextResolve(body, buffer);
                    w.WriteNumber("bytes", body.Length);
                    w.WriteString("uri", System.Text.Encoding.UTF8.GetString(buffer.Utf8(page.Uri)));
                    w.WriteNumber("tracks", page.TrackCount);
                    w.WriteStartArray("rows");
                    foreach (ref readonly Spotify.Decode.ClusterTrack t in buffer.Tracks(page.TrackStart, page.TrackCount))
                        w.WriteStringValue(System.Text.Encoding.UTF8.GetString(buffer.Utf8(t.Uri)));
                    w.WriteEndArray();
                    string next = System.Text.Encoding.UTF8.GetString(buffer.Utf8(page.NextPageUrl));
                    w.WriteString("next", next);
                    w.WriteEndObject();
                    path = next.Length > 0 ? Playback.RemotePlan.PageRoute(next) : "";
                }
                w.WriteEndArray();
            }
            finally { Spotify.Decode.ClusterBuffer.Return(buffer); }
        }
    }
}
