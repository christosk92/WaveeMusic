// ── Screens/Diagnostics.Inspect.Dj.cs ─────────────────────────────────────────────────────────────────────────────────
// the `dj` verb (AI DJ, plan-dj §2.9): what Wavee holds of a DJ list and of the lexicon session playing it.
//
//   dj                    the DJ list (its attributes as the playlist row holds them)
//   dj do=answers         the --fake lexicon service's session and first pages, decoded by the real context-resolve decoder
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
                    return Inspect.Reply.Ok(id, name, w => WriteDjList(w, uri));
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
                    return Inspect.Reply.Fail(id, name, "dj wants do=status|answers, not '" + verb + "'");
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
