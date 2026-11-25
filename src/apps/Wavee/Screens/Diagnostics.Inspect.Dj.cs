// ── Screens/Diagnostics.Inspect.Dj.cs ─────────────────────────────────────────────────────────────────────────────────
// the `dj` verb (AI DJ, plan-dj §2.9): what Wavee holds of a DJ list and of the lexicon session playing it.
//
//   dj                    the DJ list (its attributes as the playlist row holds them), the lexicon session playing (its uri,
//                         url, the pages fetched, the next page's url), and the deck and queue as `transport` reads them
//   dj do=answers         the --fake lexicon service's session and first pages, decoded by the real context-resolve decoder
//   dj do=play            start the DJ list as a click does (Playback.PlayContext): waits for the resolve and the load
//   dj do=expire          the session's lexicon_expiration_time passes now: the next page fetch resolves the session again
//
// The session readout carries what its answer stated (dj, infinite, the expiry, the three restrictions, the welcome narration's
// presence), each page's segment and correlation id, the lookahead (pages loaded after the playing one) and the DJ book: per queue
// row its page, segment, jump target, the narration slots present, the subtitle, the decision id and the attribution.
// `locks`: the context's toggle restrictions as the reducer holds them, the user's shuffle and repeat they hold back, and the
// tooltip the shuffle and repeat buttons wear.
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
                case "expire":
                    {
                        if (!Playback.Dj.ExpireForInspect()) return Inspect.Reply.Fail(id, name, "dj do=expire: no lexicon session plays");
                        return Inspect.Reply.Ok(id, name, w => { w.WriteString("do", verb); WriteDjSession(w); });
                    }
                default:
                    return Inspect.Reply.Fail(id, name, "dj wants do=status|answers|play|expire, not '" + verb + "'");
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

        /// <summary>The lexicon session playing now (none: <c>session: null</c>): what its answer stated, the pages fetched, the next
        /// page's url and the lookahead; the DJ book per queue row; then the deck and the queue as <c>transport</c> reads them.</summary>
        static void WriteDjSession(Utf8JsonWriter w)
        {
            Playback.State st = Playback.Snap();
            if (Playback.Dj.Current is not { } s) w.WriteNull("session");
            else
            {
                Playback.Dj.SessionFacts f = s.Facts;
                w.WriteStartObject("session");
                w.WriteString("context", s.Context.Text);
                w.WriteString("contextUrl", s.ContextUrl);
                w.WriteString("reason", s.Reason);
                w.WriteString("uri", f.Uri);
                w.WriteString("url", f.Url);
                w.WriteBoolean("dj", f.IsDj);
                w.WriteBoolean("infinite", f.Infinite);
                w.WriteBoolean("refusesAutoplay", s.RefusesAutoplay);
                w.WriteString("expiresAt", f.ExpiresAt > 0 ? DateTimeOffset.FromUnixTimeSeconds(f.ExpiresAt).ToString("u", System.Globalization.CultureInfo.InvariantCulture) : "");
                w.WriteBoolean("expired", f.ExpiresAt > 0 && DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= f.ExpiresAt);
                w.WriteNumber("renewals", s.Renewals);
                w.WriteStartObject("restrictions");
                w.WriteString("shuffle", f.NoShuffle);
                w.WriteString("repeatContext", f.NoRepeatContext);
                w.WriteString("repeatTrack", f.NoRepeatTrack);
                w.WriteEndObject();
                w.WriteNumber("metadataKeys", f.Metadata.Count);
                w.WriteBoolean("welcomeNarration", f.Metadata.ContainsKey("narration.welcome.ssml"));
                w.WriteString("jumpButton", f.Metadata.TryGetValue("dj.interactivity.localized_jump_button", out string? jump) ? jump : "");
                w.WriteNumber("pagesFetched", s.Pages.Count);
                w.WriteStartArray("pages");
                foreach (Playback.Dj.Page p in s.Pages)
                {
                    w.WriteStartObject();
                    w.WriteNumber("page", p.Ordinal);
                    w.WriteNumber("rows", p.Rows);
                    w.WriteString("segment", p.Segment);
                    w.WriteString("correlation", p.Correlation);
                    w.WriteString("url", p.Url);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteString("pendingUrl", s.PendingUrl);
                int ahead = Playback.Dj.PagesAhead(st.Context, global::Wavee.Queue.Rows, st.Cursor.Index);
                w.WriteStartObject("lookahead");
                w.WriteNumber("lastPage", s.LastPage);
                w.WriteNumber("pagesAhead", ahead);
                w.WriteBoolean("wantsPage", ahead >= 0 && ahead < Playback.Dj.MinPagesAhead && s.PendingUrl.Length > 0);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            // The context's toggle restrictions as the reducer holds them, the user's modes they hold back, and what the buttons say.
            w.WriteStartObject("locks");
            w.WriteString("bits", st.Locks.Bits.ToString());
            w.WriteString("reason", st.Locks.Reason.ToString());
            w.WriteString("context", st.Locks.Context.IsEmpty ? "" : st.Locks.Context.Text);
            w.WriteBoolean("holding", st.Locks.Holding);
            w.WriteBoolean("heldShuffle", st.Locks.HeldShuffle);
            w.WriteString("heldRepeat", st.Locks.HeldRepeat.ToString());
            w.WriteString("shuffleTip", PlayerVerbs.ShuffleLock(track: false) ?? "");
            w.WriteString("repeatTip", PlayerVerbs.RepeatLock(track: false) ?? "");
            w.WriteEndObject();
            w.WriteNumber("booked", Playback.Dj.Booked);
            w.WriteStartArray("book");
            ReadOnlySpan<QueueEdge> rows = global::Wavee.Queue.Rows;
            for (int i = 0; i < rows.Length; i++)
            {
                if (Playback.Dj.RowOf(rows[i].ItemId) is not { } row) continue;
                w.WriteStartObject();
                w.WriteNumber("i", i);
                w.WriteString("row", (i == st.Cursor.Index ? ">" : "") + Short(global::Wavee.Queue.RefAt(i).Id));
                w.WriteNumber("page", row.Page);
                w.WriteString("segment", row.Segment);
                w.WriteBoolean("jumpTarget", row.JumpTarget);
                w.WriteString("narration", string.Join('+', Slots(row)));
                w.WriteString("subtitle", row.Subtitle);
                w.WriteString("decision", row.DecisionId);
                w.WriteString("attribution", row.Attribution);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteString("deviceContext", Spotify.Api.DeviceContext);
            WriteTransport(w);

            static IEnumerable<string> Slots(Playback.Dj.Row row)
            {
                if (row.Intro is not null) yield return "intro";
                if (row.Jump is not null) yield return "jump";
                if (row.Outro is not null) yield return "outro";
            }
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
