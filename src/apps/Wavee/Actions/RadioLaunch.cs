using System;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Localization;
using Wavee.Core;

namespace Wavee;

// The "Start radio" action-layer glue (radio-inspiredby-mix-design §7): the backend controller is engine/UI-free and has
// no `go` callback, so the "Radio started → Open playlist" toast is raised HERE, keyed off the playlist uri
// StartRadioAsync returns. Shared by the song/artist-radio context-menu actions and the artist-hero radio pill.
static class RadioLaunch
{
    /// <summary>Fire-and-forget "Start radio": resolve + park/play the radio (Apple-Music-style, never interrupting the
    /// current track), then surface the toast. A null result (no radio / offline) shows the graceful "couldn't start
    /// radio" toast; success shows "Radio started" with an "Open playlist" action navigating to the seeded playlist.</summary>
    public static void Start(IPlaybackPlayer? player, string seedUri, string? displayName, Action<string, string?>? go)
    {
        if (player is null || string.IsNullOrEmpty(seedUri)) return;
        _ = RunAsync(player, seedUri, displayName, go);
    }

    static async Task RunAsync(IPlaybackPlayer player, string seedUri, string? displayName, Action<string, string?>? go)
    {
        string? uri;
        try { uri = await player.StartRadioAsync(seedUri, displayName).ConfigureAwait(false); }
        catch (Exception ex) { Toast.Show(ex.Message, new ToastOptions { Severity = InfoBarSeverity.Error }); return; }

        if (string.IsNullOrEmpty(uri))
        {
            Toast.Show(Loc.Get(Strings.Menu.RadioUnavailable), new ToastOptions { Severity = InfoBarSeverity.Warning });
            return;
        }

        // The action label routes through the same go("pl:" + uri, name) scheme every other "open a playlist" affordance
        // uses (Menus.AddTo, ActionRules.RouteFor), so it lands on the seeded radio playlist's detail page.
        Toast.Show(Loc.Get(Strings.Menu.RadioStarted), new ToastOptions
        {
            Severity = InfoBarSeverity.Success,
            ActionLabel = Loc.Get(Strings.Menu.OpenRadioPlaylist),
            OnAction = () => go?.Invoke("pl:" + uri, string.IsNullOrEmpty(displayName) ? Loc.Get(Strings.Menu.Radio) : displayName),
        });
    }
}
