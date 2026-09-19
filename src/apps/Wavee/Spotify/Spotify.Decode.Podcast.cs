using System.Text;
using System.Text.Json;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Decode
    {
        public static void PodcastTopics(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> uri, Staging s)
        {
            if (!uri.StartsWith("spotify:show:"u8)) return;
            var titles = new List<string>();
            var r = new ProtoReader(bytes);
            while (r.Next())
                if (r.Field == 1) titles.Add(Encoding.UTF8.GetString(r.Message().Bytes(2))); else r.Skip();
            ref var row = ref s.Shows.RowFor(Identity(s, uri), Authority.Full, (uint)ShowFields.Topics);
            row.Topics = s.AddText(Encoding.UTF8.GetBytes(string.Join('\n', titles)));
            s.Shows.Settle();
        }

        public static void PodcastHtml(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> uri, Staging s)
        {
            if (!uri.StartsWith("spotify:show:"u8)) return;
            ref var row = ref s.Shows.RowFor(Identity(s, uri), Authority.Full, (uint)ShowFields.Html);
            row.HtmlDescription = s.AddText(new ProtoReader(bytes).Bytes(2));
            s.Shows.Settle();
        }

        public static void PodcastAppearance(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> uri, Staging s)
        {
            if (!uri.StartsWith("spotify:show:"u8)) return;
            var r = new ProtoReader(bytes);
            var scheme = ColorScheme(r.Sub(2).Sub(1));
            ref var row = ref s.Shows.RowFor(Identity(s, uri), Authority.Full, (uint)ShowFields.Appearance);
            row.Tone = scheme.BackgroundTintedBase;
            s.Shows.Settle();
        }

        public static void EpisodeMedia(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> uri, Staging s)
        {
            if (!uri.StartsWith("spotify:episode:"u8)) return;
            ref var row = ref s.Episodes.RowFor(Identity(s, uri), Authority.Full, (uint)EpisodeFields.Media);
            var r = new ProtoReader(bytes);
            while (r.Next())
            {
                if (r.Field == 1)
                {
                    var duration = Google.Protobuf.WellKnownTypes.Duration.Parser.ParseFrom(r.Bytes());
                    row.DurationMs = (int)Math.Clamp(duration.Seconds * 1000 + duration.Nanos / 1_000_000, 0, int.MaxValue);
                }
                else if (r.Field == 2) { if (PackedEnumContains(r.Bytes(), 1)) row.Flags |= (uint)EpisodeFlags.Explicit; }
                else if (r.Field == 4) { if (PackedEnumContains(r.Bytes(), 2)) row.Flags |= (uint)EpisodeFlags.Video; }
                else r.Skip();
            }
            s.Episodes.Settle();
        }

        static bool PackedEnumContains(ReadOnlySpan<byte> bytes, uint expected)
        {
            uint value = 0; int shift = 0;
            foreach (byte b in bytes)
            {
                if (shift >= 35) return false;
                value |= (uint)(b & 127) << shift;
                if ((b & 128) != 0) { shift += 7; continue; }
                if (value == expected) return true;
                value = 0; shift = 0;
            }
            return false;
        }

        public static void PodcastRating(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> uri, Staging s)
        {
            if (!uri.StartsWith("spotify:show:"u8)) return;
            ref var row = ref s.Shows.RowFor(Identity(s, uri), Authority.Full, (uint)ShowFields.Rating);
            var r = new ProtoReader(bytes);
            while (r.Next())
            {
                if (r.Field == 1)
                {
                    var summary = r.Message(); double average = 0; bool visible = false;
                    while (summary.Next())
                        switch (summary.Field)
                        {
                            case 1: average = summary.Double(); break;
                            case 2: row.RatingCount = summary.Int32(); break;
                            case 3: visible = summary.Bool(); break;
                            default: summary.Skip(); break;
                        }
                    row.RatingX100 = visible ? (ushort)Math.Clamp(Math.Round(average * 100), 0, 500) : (ushort)0;
                }
                else if (r.Field == 2)
                {
                    var mine = r.Message();
                    while (mine.Next())
                        if (mine.Field == 3) row.MyRating = (byte)Math.Clamp(mine.Int32(), 0, 5); else mine.Skip();
                }
                else if (r.Field == 3) { if (r.Bool()) row.Flags |= (uint)ShowFlags.CanRate; }
                else r.Skip();
            }
            s.Shows.Settle();
        }

        public static void EpisodeTranscripts(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> uri, Staging s)
        {
            if (!uri.StartsWith("spotify:episode:"u8)) return;
            ref var row = ref s.Episodes.RowFor(Identity(s, uri), Authority.Full, (uint)EpisodeFields.Transcript);
            var r = new ProtoReader(bytes);
            while (r.Next())
            {
                if (r.Field != 2) { r.Skip(); continue; }
                var transcript = r.Message(); TextRef cdn = default, along = default, language = default;
                while (transcript.Next())
                    switch (transcript.Field)
                    {
                        case 2: language = s.AddText(transcript.Bytes()); break;
                        case 4: cdn = s.AddText(transcript.Bytes()); break;
                        case 6: along = s.AddText(transcript.Bytes()); break;
                        default: transcript.Skip(); break;
                    }
                if (row.TranscriptUrl.IsEmpty && (!along.IsEmpty || !cdn.IsEmpty))
                {
                    row.TranscriptUrl = along.IsEmpty ? cdn : along;
                    row.TranscriptLanguage = language;
                    row.TranscriptReadAlong = !along.IsEmpty;
                    row.Flags |= (uint)EpisodeFlags.HasTranscript;
                }
            }
            s.Episodes.Settle();
        }

        // This web selection owns detail, never progress. Desktop NPV has the same root with fewer fields.
        public static void EpisodeDetail(byte[] bytes, string uri, Staging s)
        {
            using var doc = JsonDocument.Parse(bytes);
            var episode = Podcasts.At(doc.RootElement, "data", "episodeUnionV2");
            if (Podcasts.Text(episode, "__typename") != "Episode") return;
            ref var row = ref s.Episodes.RowFor(Identity(s, Encoding.UTF8.GetBytes(uri)), Authority.Full, (uint)EpisodeFields.Detail);
            row.HtmlDescription = s.AddText(Encoding.UTF8.GetBytes(Podcasts.Text(episode, "htmlDescription")));
            if (Podcasts.Bool(Podcasts.At(episode, "restrictions"), "paywallContent")) row.Flags |= (uint)EpisodeFlags.Paywalled;
            bool unplayable = Podcasts.At(episode, "playability", "playable").ValueKind == JsonValueKind.False;
            if (unplayable) row.Flags |= (uint)EpisodeFlags.Unplayable;
            if (unplayable
                && Podcasts.Text(Podcasts.At(episode, "previewPlayback", "audioPreview"), "cdnUrl").Length > 0)
                row.Flags |= (uint)EpisodeFlags.PreviewOnly;
            s.Episodes.Settle();
        }
    }
}
