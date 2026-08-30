using System.IO;
using System.Text;
using Wavee.Core.ReleaseNotes;

namespace Wavee.ReleaseTool;

/// <summary>
/// Writes the two human-facing texts: <c>RELEASE_BODY.md</c> (the GitHub release body, passed to
/// <c>gh release create --notes-file</c>) and <c>store-listing.txt</c> (the blurb a store listing wants).
/// Both are rendered by <see cref="ReleaseNotesValidation"/> so the rules are unit-tested.
/// </summary>
static class BodyWriter
{
    static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void Write(string outDir, ReleaseNotesDocument doc, string repo, string? generatedNotes)
    {
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "RELEASE_BODY.md"),
            ReleaseNotesValidation.RenderBody(doc, repo, generatedNotes), Utf8NoBom);
        File.WriteAllText(Path.Combine(outDir, "store-listing.txt"),
            ReleaseNotesValidation.RenderStoreListing(doc), Utf8NoBom);
    }
}
