using System.Text.Json;
using System.Text.Json.Serialization;
using Wavee.Core.Video;
using Wavee.UI.Services.Actions;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Services.SpotifyVideo;
using Wavee.UI.WinUI.Views;

namespace Wavee.UI.WinUI.Json;

/// <summary>
/// Single AOT-friendly <see cref="JsonSerializerContext"/> for every
/// reflection-driven JSON callsite in <c>Wavee.UI.WinUI</c>. Adding a new
/// non-context-driven Serialize / Deserialize anywhere in this assembly will
/// trip <c>IL2026</c> / <c>IL3050</c> as errors; the fix is to add the target
/// type here and route the callsite through <c>WaveeUiWinUiJsonContext.Default.&lt;Type&gt;</c>.
///
/// <para>
/// For other contexts in the graph see:
/// </para>
/// <list type="bullet">
///   <item><c>Wavee.UI.Json.WaveeUiJsonContext</c> — undoable-action payloads.</item>
///   <item><c>Wavee.Local.LocalLibraryJsonContext</c> — <c>MetadataPatch</c> persisted by <c>LocalLibraryService</c>.</item>
///   <item>Existing per-feature contexts (HomeJsonContext, LyricsCacheJsonContext, AppSettingsJsonContext, AlbumTrackResultJsonContext, UiHandoffJsonContext) — leave in place.</item>
/// </list>
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(CrashRecoveryReport))]
[JsonSerializable(typeof(UserActionDescriptor))]
[JsonSerializable(typeof(HomeDebugMissingSectionPayload))]
[JsonSerializable(typeof(HomeDebugSectionViewModel))]
[JsonSerializable(typeof(JsonElement))]
// Web-EME video player surfaces (SpotifyWebEmePlayer / DocumentRenderer).
[JsonSerializable(typeof(SpotifyWebEmeVideoManifest))]
[JsonSerializable(typeof(WebEmeLicenseResponseMessage))]
[JsonSerializable(typeof(WebEmeLicenseErrorMessage))]
// Shell-session restorable navigation parameters. Only the known set is
// supported; arbitrary types round-tripped through ShellSessionService are
// returned as null on restore (the parameter is lost, the page still opens).
[JsonSerializable(typeof(CreatePlaylistParameter))]
internal sealed partial class WaveeUiWinUiJsonContext : JsonSerializerContext
{
}
