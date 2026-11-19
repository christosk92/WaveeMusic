// ── Screens/Diagnostics.Inspect.PodcastPages.cs ─────────────────────────────────────────────────────────────────────
// the `podcastpages` verb: load a podcast/audiobook pages fixture into the fake scope and dump its view model
// (Entities/PodcastPages.Fixtures.cs). The pages lane mounts, loads a fixture, then `goto uri=<show>`.

using System.Text;
using System.Text.Json;

namespace Wavee;

public static partial class Diagnostics
{
    public sealed partial class InspectSession
    {
        string PodcastPagesVerb(int id, string name, Inspect.Args a)
        {
            string fixture = a.Has("fixture") ? a.Str("fixture") : "book-titled";
            var show = PodcastPages.LoadFixture(fixture, out string error);
            if (!show.IsValid) return Inspect.Reply.Fail(id, name, error.Length > 0 ? error : "the fixture named no resident show");
            PodcastPages.Open(show);
            if (a.Has("path"))
            {
                using var buffer = new MemoryStream();
                using (var file = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
                {
                    file.WriteStartObject();
                    file.WriteString("fixture", fixture);
                    file.WriteString("show", show.Uri.Text);
                    PodcastPages.WriteDump(file, show);
                    file.WriteEndObject();
                }
                File.WriteAllText(a.Str("path"), Encoding.UTF8.GetString(buffer.ToArray()));
            }
            return Inspect.Reply.Ok(id, name, w =>
            {
                w.WriteString("fixture", fixture);
                w.WriteString("show", show.Uri.Text);
                PodcastPages.WriteDump(w, show);
            });
        }
    }
}
