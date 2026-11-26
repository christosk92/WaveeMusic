// ── Spotify/Spotify.Decode.AutomixPreview.cs — the editor's Connect preview (#48 Mix M5, protocol §5) ──────────────────
//
// The official editor previews a transition on the ACTIVE device: a Connect `signal` whose `signal_id` is `automix-preview`
// and whose `parameters` is a base64 `AutomixPreview` protobuf (the client's field names):
//
//   AutomixPreview { 1 trackUri1  2 trackUri2  3 automixMode  4 transitionUri  5 startPositionMs  6 relativeStartPosition
//                    7 cuepoints  8 stopPositionMs  9 transitionRecipe (TransitionRecipe, §4)  10 playableTrackUri1
//                    11 playableTrackUri2  12 contextUri  13 armId  14 itemSpeedA (double)  15 itemSpeedB (double) }
//
// `stop-automix-preview` carries "true" or "false" (resume afterwards or not): seen neither on the wire nor its encoding, so the
// decoder takes the bare word, the word as a JSON string or its base64. Both directions live here: the receiver's decode
// (Spotify.Connect.Commands routes the signal) and the editor's encode (Spotify.Connect.Outbound sends it). PURE.

using System.Text.Json;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Decode
    {
        /// <summary>The two Mix preview signals a Connect <c>signal</c> can carry; <see cref="None"/> for any other id.</summary>
        public enum MixPreviewSignal : byte { None, Preview, Stop }

        /// <summary>The wire's <c>signal_id</c> of a Mix preview signal.</summary>
        public static string SignalId(MixPreviewSignal signal) => signal == MixPreviewSignal.Stop ? "stop-automix-preview" : "automix-preview";

        /// <summary>One decoded <c>AutomixPreview</c>. Strings: a preview is a click in an editor, never per frame.</summary>
        public sealed record AutomixPreview(string TrackA, string TrackB, string Mode, string TransitionUri, long StartPositionMs,
            bool RelativeStart, long StopPositionMs, byte[] Recipe, string PlayableA, string PlayableB, string ContextUri,
            double ItemSpeedA, double ItemSpeedB)
        {
            /// <summary>The track that plays first: the playable uri when the editor named one (a relinked track), else 1.</summary>
            public string A => PlayableA.Length > 0 ? PlayableA : TrackA;
            public string B => PlayableB.Length > 0 ? PlayableB : TrackB;
        }

        /// <summary>The <c>pre-roll</c> every captured preview carried (protocol §5: 3000, relative to the out-point).</summary>
        public const long AutomixPreRollMs = 3_000;

        /// <summary>A Connect <c>signal</c> REQUEST body → which Mix preview signal it is, and its payload: the preview, or
        /// <paramref name="resume"/> for a stop (true when the body says nothing). <see cref="MixPreviewSignal.None"/> for every
        /// other signal (the video switches) or a preview whose parameters do not decode.</summary>
        public static MixPreviewSignal MixPreviewCommand(ReadOnlySpan<byte> payload, out AutomixPreview? preview, out bool resume)
        {
            preview = null;
            resume = true;
            var r = new Utf8JsonReader(payload);
            string? signalId = null, parameters = null;
            try
            {
                while (r.Read())
                {
                    if (r.TokenType != JsonTokenType.PropertyName) continue;
                    if (r.ValueTextEquals("signal_id"u8)) { r.Read(); signalId = r.TokenType == JsonTokenType.String ? r.GetString() : null; }
                    else if (r.ValueTextEquals("parameters"u8))
                    {
                        r.Read();
                        parameters = r.TokenType switch
                        {
                            JsonTokenType.String => r.GetString(),
                            JsonTokenType.True => "true",
                            JsonTokenType.False => "false",
                            _ => null,
                        };
                        if (r.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) r.Skip();
                    }
                }
            }
            catch (JsonException) { }
            if (signalId == "stop-automix-preview")
            {
                resume = StopResumes(parameters);
                return MixPreviewSignal.Stop;
            }
            if (signalId != "automix-preview" || string.IsNullOrEmpty(parameters)) return MixPreviewSignal.None;
            byte[] proto;
            try { proto = Convert.FromBase64String(parameters); }
            catch (FormatException) { return MixPreviewSignal.None; }
            preview = AutomixPreviewOf(proto);
            return preview is null ? MixPreviewSignal.None : MixPreviewSignal.Preview;
        }

        /// <summary>A stop's payload → resume afterwards: "false" (bare, quoted or base64) says no; anything else, or nothing, yes.</summary>
        static bool StopResumes(string? parameters)
        {
            if (string.IsNullOrEmpty(parameters)) return true;
            string word = parameters.Trim().Trim('"');
            if (word.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            if (word.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            try
            {
                string decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(word)).Trim().Trim('"');
                return !decoded.Equals("false", StringComparison.OrdinalIgnoreCase);
            }
            catch (FormatException) { return true; }
        }

        /// <summary>The <c>AutomixPreview</c> protobuf → its value; null without the two tracks.</summary>
        public static AutomixPreview? AutomixPreviewOf(ReadOnlySpan<byte> proto)
        {
            string a = "", b = "", mode = "", transition = "", playA = "", playB = "", context = "";
            long start = AutomixPreRollMs, stop = 0;
            bool relative = false;
            double speedA = 1, speedB = 1;
            byte[] recipe = [];
            var r = new ProtoReader(proto);
            while (r.Next())
            {
                switch (r.Field)
                {
                    case 1 when r.Wire == 2: a = Utf8(r.Bytes()); break;
                    case 2 when r.Wire == 2: b = Utf8(r.Bytes()); break;
                    case 3 when r.Wire == 2: mode = Utf8(r.Bytes()); break;
                    case 4 when r.Wire == 2: transition = Utf8(r.Bytes()); break;
                    case 5 when r.Wire == 0: start = (long)r.Varint(); break;
                    case 6 when r.Wire == 0: relative = r.Bool(); break;
                    case 8 when r.Wire == 0: stop = (long)r.Varint(); break;
                    case 9 when r.Wire == 2: recipe = r.Bytes().ToArray(); break;
                    case 10 when r.Wire == 2: playA = Utf8(r.Bytes()); break;
                    case 11 when r.Wire == 2: playB = Utf8(r.Bytes()); break;
                    case 12 when r.Wire == 2: context = Utf8(r.Bytes()); break;
                    case 14 when r.Wire == 1: speedA = r.Double(); break;
                    case 15 when r.Wire == 1: speedB = r.Double(); break;
                    default: r.Skip(); break;
                }
            }
            if ((a.Length == 0 && playA.Length == 0) || (b.Length == 0 && playB.Length == 0)) return null;
            return new AutomixPreview(a, b, mode, transition, start, relative, stop, recipe, playA, playB, context, speedA, speedB);

            static string Utf8(ReadOnlySpan<byte> bytes) => System.Text.Encoding.UTF8.GetString(bytes);
        }

        /// <summary>An <c>AutomixPreview</c> as the official editor sends it (the fields its 15 previews carried, in field order):
        /// the two tracks, mode "auto", the pre-roll relative to the out-point, the recipe, the playable uris, the context and the
        /// item speeds (1.0). The editor's side of the preview (Spotify.Connect.Outbound). PURE.</summary>
        public static byte[] EncodeAutomixPreview(AutomixPreview p)
        {
            byte[] buffer = new byte[256 + p.Recipe.Length + 6 * (p.TrackA.Length + p.TrackB.Length + p.ContextUri.Length + 32)];
            var w = new ProtoWriter(buffer);
            w.Str(1, p.TrackA);
            w.Str(2, p.TrackB);
            w.Str(3, p.Mode.Length > 0 ? p.Mode : "auto");
            w.Str(4, p.TransitionUri);
            w.UAlways(5, (ulong)Math.Max(0, p.StartPositionMs));
            w.B(6, p.RelativeStart);
            w.U(8, (ulong)Math.Max(0, p.StopPositionMs));
            if (p.Recipe.Length > 0)
            {
                w.Tag(9, 2);
                w.Var((ulong)p.Recipe.Length);
                w.Raw(p.Recipe);
            }
            w.Str(10, p.PlayableA);
            w.Str(11, p.PlayableB);
            w.Str(12, p.ContextUri);
            w.F64(14, p.ItemSpeedA);
            w.F64(15, p.ItemSpeedB);
            return buffer.AsSpan(0, w.Length).ToArray();
        }
    }
}
