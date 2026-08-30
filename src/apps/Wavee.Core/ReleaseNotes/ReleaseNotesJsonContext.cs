using System.Text.Json.Serialization;

namespace Wavee.Core.ReleaseNotes;

/// <summary>The one AOT-safe JSON context for the release-notes wire, shared by the app (read) and
/// <c>Wavee.ReleaseTool</c> (read + write). Source-generated: no reflection, no runtime metadata.
/// <para>camelCase on the wire, indented so a hand-authored <c>whatsnew.json</c> round-trips readably, and nulls
/// omitted so an optional member (poster, deepLink, stateReason, scope) simply is not there. Unknown members are
/// ignored by default, which is how schema 1 stays forward-compatible.</para></summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ReleaseNotesDocument))]
[JsonSerializable(typeof(ReleaseNotesIndex))]
[JsonSerializable(typeof(IssueStateCache))]
public partial class ReleaseNotesJsonContext : JsonSerializerContext
{
}
