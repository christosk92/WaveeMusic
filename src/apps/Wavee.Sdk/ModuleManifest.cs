using System.Text.Json.Serialization;

namespace Wavee.Sdk;

/// <summary>
/// The <c>wavee-module.json</c> document that sits next to a module's entry point and tells the host what the
/// module is, which protocol version it speaks and which links it claims. Discovered by the host in
/// <c>&lt;app dir&gt;\modules\&lt;id&gt;\</c> and <c>%LOCALAPPDATA%\Wavee\modules\&lt;id&gt;\&lt;version&gt;\</c>.
/// </summary>
/// <param name="SchemaVersion">Version of the manifest document itself (currently 1); versioned independently of <paramref name="ProtocolVersion"/>.</param>
/// <param name="Id">Stable module id in <c>publisher.name</c> form (ASCII, at most 128 chars).</param>
/// <param name="Version">Module version string (semver-ish); the highest version per id wins during discovery.</param>
/// <param name="DisplayName">Human-readable name shown in menus and on the diagnostics page.</param>
/// <param name="Publisher">Publisher key; <c>"wavee"</c> marks a first-party, bundled module.</param>
/// <param name="ProtocolVersion">The wire-protocol version this module speaks (see <see cref="Protocol.ModuleProtocol"/>).</param>
/// <param name="Entry">Executable or <c>.dll</c> to launch, relative to the module directory (never escapes it).</param>
/// <param name="Capabilities">Declared capabilities, e.g. <c>playback</c>, <c>match</c>, <c>metadata</c>, <c>fallback</c>.</param>
/// <param name="UrlPatterns">Host substrings used as a cheap prefilter before the process is spawned.</param>
/// <param name="Menu">Optional "Play ▸" submenu entry contributed by this module.</param>
public sealed record ModuleManifest(
    int SchemaVersion,
    string Id,
    string Version,
    string DisplayName,
    string Publisher,
    int ProtocolVersion,
    string Entry,
    string[] Capabilities,
    string[] UrlPatterns,
    ModuleMenu? Menu);

/// <summary>A module's row in the profile menu's "Play ▸" submenu.</summary>
/// <param name="Label">Menu label, e.g. <c>"YouTube…"</c>.</param>
/// <param name="Placeholder">Placeholder text for the paste-a-link dialog, e.g. <c>"Paste a YouTube link"</c>.</param>
public sealed record ModuleMenu(string Label, string Placeholder)
{
    /// <summary>Optional localization key that overrides <see cref="Label"/> when the host can resolve it.</summary>
    [JsonPropertyName("labelLocKey")]
    public string? LabelLocKey { get; init; }
}
