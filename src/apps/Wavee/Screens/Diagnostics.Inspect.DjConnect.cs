// ── Screens/Diagnostics.Inspect.DjConnect.cs ──────────────────────────────────────────────────────────────────────────
// the `dj` verb's Connect half (AI DJ, plan-dj D4): what Wavee sends and reads about a DJ over Connect, headless.
//
//   dj do=envelope [uri=]   the `play` body a DJ start (or any context's start) sends the device that owns playback, through the
//                           one send's own shaping and writer, with fixed ids and clock (nothing is sent)
//
// Role: SHELL (UI thread)

using System.Text.Json;

namespace Wavee;

public static partial class Diagnostics
{
    public sealed partial class InspectSession
    {
        /// <summary>The Connect sub-verbs' names, as the verb table lists them.</summary>
        internal const string DjConnectVerbs = "envelope";

        /// <summary>The Connect sub-verbs of `dj`; null when <paramref name="verb"/> is not one of them.</summary>
        string? DjConnectVerb(int id, string name, string verb, string uri, Inspect.Args a)
        {
            switch (verb)
            {
                case "envelope":
                    {
                        if (!EntityId.TryParse(uri.AsSpan(), out EntityId context)) return Inspect.Reply.Fail(id, name, "not a uri: " + uri);
                        string body = Playback.ForwardEnvelopeForInspect(context);
                        return Inspect.Reply.Ok(id, name, w =>
                        {
                            w.WriteString("do", verb);
                            w.WriteString("uri", uri);
                            w.WritePropertyName("envelope");
                            using JsonDocument doc = JsonDocument.Parse(body);
                            doc.RootElement.WriteTo(w);
                        });
                    }
                default:
                    return null;
            }
        }
    }
}
