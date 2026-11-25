// ── Entities/Entities.Fake.Dj.cs — FAKE (AI DJ, plan-dj §2.9 "--fake") ────────────────────────────────────────────────
//
// One DJ list for --fake and the inspector, and the lexicon session provider's answers for it, in the shape the live service
// answers (plan-dj §1.2–§1.3): a list whose format attributes carry `lexicon_set_type=your_dj` and a `lexicon_context_url`; a session
// answer (root uri, url, the context metadata, one empty page naming the first real page, the three `dj-disallow` restrictions);
// and pages of 5-track segments whose `next_page_url` names the next page, endlessly. Every id, segment, decision id, request id
// and spoken line here is INVENTED; the tracks are the demo catalogue's own. `Playback.Dj.UseFake` (App.cs, --fake) serves
// them as the response BODY of the lexicon GET, so the real resolver, the real decoder and the real paging run against them.
// Route: pl:spotify:playlist:pldj.

using System.Globalization;
using System.Text.Json;

namespace Wavee;

public static partial class Entities
{
    internal static partial class LibrarySeed
    {
        /// <summary>The fake DJ list (never the live one's id: a DJ is known by its attributes, plan-dj §1.1).</summary>
        public const string DjUri = "spotify:playlist:pldj";

        /// <summary>Its <c>lexicon_context_url</c> attribute, as the rootlist and the list read carry it.</summary>
        public const string DjLexiconUrl = "hm://lexicon-session-provider/context-resolve/v2/session?contextUri=" + DjUri;

        static void StageDj(Staging s)
        {
            ref var row = ref s.Playlists.RowFor(Text(s, DjUri), Authority.Seed, (uint)PlaylistFields.Identity);
            row.Title = Text(s, "DJ");
            row.Description = Text(s, "Your own AI DJ: the music you love, and what you'll love next, with a voice between the songs.");
            row.Image = Text(s, Cover(11));
            row.OwnerUri = Text(s, SpotifyUserUri);
            row.Flags = (uint)PlaylistFlags.YourDj;
            row.FlagsMask = (uint)PlaylistFlags.YourDj;
            row.LexiconUrl = Text(s, DjLexiconUrl);
            // The card's words and colours (home.card.*), as the rootlist and the Home feed carry them (the bundled Home capture's DJ card is
            // this list too: home.json names spotify:playlist:pldj, so its card plays the fake session).
            row.DjTranscript = Text(s, "Hey, it's your DJ. Got a run of your favourites ready to go.");
            row.DjColors = new DjCardColors(Background: 0xFF1B3A6Bu, Top: 0xFF3FD8A0u, Mid: 0xCB3FC8D0u, Bottom: 0x7F3F90D8u, Text: 0xFFFFFFFFu);
        }

        /// <summary>The DJ list has no rows of its own (the service lists none): its membership is known and empty.</summary>
        static void LandDj()
        {
            var p = new global::Wavee.Playlist(SlotOf(EntityKind.Playlist, DjUri));
            if (p.IsValid) Land(p, [], []);
        }
    }

    // ── the lexicon session provider's answers (served by Playback.Dj.UseFake) ────────────────────────────────────

    const string LexiconSessionPath = "/lexicon-session-provider/context-resolve/v2/session";
    const string DjPagePath = LexiconSessionPath + "/00000000000000000000000000000000";
    const int DjPoolOffset = 300;
    const int DjSegmentBase = 4100;
    static readonly string[] s_djSubtitles = ["Vibes", "From the past", "Based on recent listening", "Late-night picks"];

    /// <summary>How many lexicon answers the fake has served (the inspector's evidence that a fetch happened).</summary>
    public static int FakeLexiconServed { get; private set; }

    /// <summary>The highest page served so far: a session answered again (a renewal past its expiry, a second start) begins after it,
    /// so its rows carry new uids, segments and decision ids as a fresh session's do.</summary>
    static int s_djHighestPage;

    /// <summary>The fake service's answer to a GET of <paramref name="path"/> (the spclient path the resolver sends, query included),
    /// or null for a path the fake does not serve. The session for the fake DJ list, and its page <c>n</c> (1-based, read back off the
    /// request id the previous answer wrote) for any n: the session is endless. A session answer names the page after the highest
    /// one served, so every page of every session is distinct.</summary>
    internal static byte[]? FakeLexicon(string path)
    {
        if (!path.StartsWith(LexiconSessionPath, StringComparison.Ordinal)) return null;
        if (path.IndexOf("contextUri=" + LibrarySeed.DjUri, StringComparison.Ordinal) < 0) return null;
        byte[]? body = path.StartsWith(DjPagePath, StringComparison.Ordinal) ? DjPage(PageOf(path))
            : path.StartsWith(LexiconSessionPath + "?", StringComparison.Ordinal) ? DjSession(path) : null;
        if (body is not null) FakeLexiconServed++;
        return body;
    }

    /// <summary>The page a page url asks for: the request id the fake wrote (<see cref="RequestId"/>) ends in the page's number.</summary>
    static int PageOf(string path)
    {
        int at = path.IndexOf("requestId=", StringComparison.Ordinal);
        if (at < 0) return 0;
        ReadOnlySpan<char> id = path.AsSpan(at + "requestId=".Length);
        int end = id.IndexOf('&');
        if (end >= 0) id = id[..end];
        return id.Length > 8 && int.TryParse(id[^8..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int n) ? n : 0;
    }

    /// <summary>An invented 32-hex request id that names page <paramref name="page"/> (the service writes its own; the client never
    /// builds one).</summary>
    static string RequestId(int page) => "fa4ed1ce000000000000" + "0000" + page.ToString("x8", CultureInfo.InvariantCulture);

    static string SegmentId(int page) => "FAKE-Segment-" + (DjSegmentBase + page).ToString(CultureInfo.InvariantCulture);

    /// <summary>The url of page <paramref name="page"/>: the first names no previous segment, the rest the one before.</summary>
    static string DjPageUrl(int page, bool first = false)
        => "hm:/" + DjPagePath + "?contextUri=" + LibrarySeed.DjUri
           + (first ? "&opmPlayed=false" : "&previousSegmentId=" + SegmentId(page - 1))
           + "&requestId=" + RequestId(page);

    static string Hex(int value, int width) => value.ToString("x" + width.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    static byte[] DjSession(string path)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("uri", LibrarySeed.DjUri);
            w.WriteString("url", "hm://" + path.TrimStart('/'));
            w.WriteStartObject("metadata");
            string now = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            string expires = DateTimeOffset.UtcNow.AddHours(12).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            w.WriteString("lexicon_set_type", "your_dj");
            w.WriteString("agentic_product_type", "dj");
            w.WriteString("context_description", "DJ");
            w.WriteString("automix.mode", "radio");
            w.WriteString("automix.talk_mode", "human_cuepoints_or_full_track");
            w.WriteString("shuffle.algorithm", "none");
            w.WriteString("autoplay_candidate", "false");
            w.WriteString("end_of_context_action", "stop");
            w.WriteString("add_context_load_reason", "true");
            w.WriteString("lexicon_current_time", now);
            w.WriteString("lexicon_expiration_time", expires);
            w.WriteString("dj.interactivity_enabled", "true");
            w.WriteString("dj.interactivity.localized_cta", "Your DJ here. What can I play for you?");
            w.WriteString("dj.interactivity.localized_jump_button", "Let DJ pick");
            w.WriteString("narration.background.color", "#FF1B3A6B");
            w.WriteString("narration.jellyfish.top.color", "#FF3FD8A0");
            w.WriteString("narration.jellyfish.mid.color", "#FF3FC8D0");
            w.WriteString("narration.jellyfish.bottom.color", "#FF3F90D8");
            w.WriteString("narration.jump.background.color", "#FF1B3A6B");
            w.WriteString("narration.jump.border.color", "#FF1B3A6B");
            w.WriteString("narration.jump.icon.color", "#FF3FD8A0");
            Narration(w, "welcome", "Welcome", "dj~fa4e0000000000000000000000000000we1c", "",
                "<speak xml:lang=\"en-US\">Hey there, it's your DJ. Let's ease into it.</speak>");
            w.WriteEndObject();
            w.WriteStartArray("pages");
            w.WriteStartObject();
            w.WriteString("next_page_url", DjPageUrl(s_djHighestPage + 1, first: true));
            w.WriteStartArray("tracks");
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteStartObject("restrictions");
            foreach (string key in (ReadOnlySpan<string>)["disallow_toggling_repeat_context_reasons", "disallow_toggling_repeat_track_reasons",
                         "disallow_toggling_shuffle_reasons"])
            {
                w.WriteStartArray(key);
                w.WriteStringValue("dj-disallow");
                w.WriteEndArray();
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return stream.ToArray();
    }

    /// <summary>Page <paramref name="page"/> (1-based): one 5-track segment of the demo catalogue, its first track the jump target
    /// with the intro narration (and, from page 2 on, the jump narration), its last the outro.</summary>
    static byte[] DjPage(int page)
    {
        page = Math.Max(1, page);
        s_djHighestPage = Math.Max(s_djHighestPage, page);
        string segment = SegmentId(page);
        string decision = "ssp~fa4e" + Hex(page, 32);
        string subtitle = s_djSubtitles[Wrap(page - 1, s_djSubtitles.Length)];
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("next_page_url", DjPageUrl(page + 1));
            w.WriteStartArray("tracks");
            for (int slot = 1; slot <= 5; slot++)
            {
                int pool = DjPoolOffset + (page - 1) * 5 + (slot - 1);
                w.WriteStartObject();
                w.WriteString("uri", TrackUri(pool));
                w.WriteString("uid", "df" + Hex(page, 10) + Hex(slot, 8));
                w.WriteStartObject("metadata");
                w.WriteString("station_title", "DJ");
                w.WriteString("station_subtitle", subtitle);
                w.WriteString("station_uri", "");
                w.WriteString("segment", segment);
                w.WriteString("source.components", "YourDJ," + segment);
                w.WriteString("decision_id", decision);
                w.WriteString("custom_reporting_attribution", segment + ":" + (page - 1).ToString(CultureInfo.InvariantCulture) + ":"
                                                               + slot.ToString(CultureInfo.InvariantCulture) + ":0");
                w.WriteString("album_image_small", Cover(pool));
                w.WriteString("album_image_large", Cover(pool));
                w.WriteString("agentic_product_type", "dj");
                if (slot == 1)
                {
                    w.WriteString("dj.jump_target", "true");
                    w.WriteString("automix.talk_mode", "human_cuepoints_or_full_track");
                    Narration(w, "intro", "Up next", "dj~fa4e" + Hex(page, 28) + "1a7e", segment + ":" + (page - 1).ToString(CultureInfo.InvariantCulture) + ":0:0",
                        "<speak xml:lang=\"en-US\">Coming up, a run of " + subtitle.ToLowerInvariant()
                        + " songs, starting with <entity type=\"artist\" uri=\"ar" + (page % 7).ToString(CultureInfo.InvariantCulture)
                        + "\">one of your favourites</entity>.</speak>");
                    if (page >= 2)
                        Narration(w, "jump", "Up next", "dj~fa4e" + Hex(page, 28) + "0a1b", segment + ":" + (page - 1).ToString(CultureInfo.InvariantCulture) + ":0:0",
                            "<speak xml:lang=\"en-US\">OK, let's switch it up. Here's something " + subtitle.ToLowerInvariant() + ".</speak>");
                }
                if (slot == 5)
                {
                    w.WriteString("automix.talk_mode", "human_cuepoints_or_full_track");
                    Narration(w, "outro", "Up next", "dj~fa4e" + Hex(page, 28) + "0b70", segment + ":" + (page - 1).ToString(CultureInfo.InvariantCulture) + ":6:0",
                        "<speak xml:lang=\"en-US\">That one closes out the set.</speak>");
                }
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartObject("metadata");
            w.WriteString("correlation-id", decision);
            w.WriteString("lastComponentId", segment);
            w.WriteString("automix.mode", "radio");
            w.WriteString("narration.background.color", "#FF1B3A6B");
            w.WriteString("narration.jump.background.color", "#FF1B3A6B");
            w.WriteString("narration.jump.border.color", "#FF1B3A6B");
            w.WriteString("narration.jump.icon.color", "#FF3FD8A0");
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return stream.ToArray();
    }

    /// <summary>One narration slot's keys (<c>narration.&lt;slot&gt;.*</c>, plan-dj §1.3), every value invented.</summary>
    static void Narration(Utf8JsonWriter w, string slot, string title, string decision, string attribution, string ssml)
    {
        string p = "narration." + slot + ".";
        w.WriteString(p + "ssml", ssml);
        w.WriteString(p + "title", title);
        w.WriteString(p + "artist", "DJ");
        w.WriteString(p + "album_title", "DJ");
        w.WriteString(p + "image", Cover(11));
        w.WriteString(p + "voice", "VOICE1");
        w.WriteString(p + "tts_provider", "SONANTIC_FAST");
        w.WriteString(p + "sample_rate", "44100");
        w.WriteString(p + "loudness", "-16.0");
        w.WriteString(p + "true_peak", "-3.0");
        w.WriteString(p + "commentary_type", "NARRATION");
        w.WriteString(p + "commentary_id", "");
        w.WriteString(p + "decision_id", decision);
        if (attribution.Length > 0) w.WriteString(p + "custom_reporting_attribution", attribution);
    }
}
