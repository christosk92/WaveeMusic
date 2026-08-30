using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Input;
using FluentGpu.Localization;
using Wavee.Core;

namespace Wavee;

/// <summary>
/// The ONE way a playlist is created. Every surface that offers "New playlist" — the two menu deposits, the picker's
/// inline row, the sidebar create row, the drop-to-create target and all three sidebar designs — calls
/// <see cref="Create(ActionServices, RootlistPlacement, bool)"/>, so the name, the placement, the navigation and the
/// failure story are decided once instead of six times.
///
/// <para><b>Why it is synchronous.</b> The P3 seam's <c>CreatePlaylist</c> puts the optimistic header, the empty
/// membership and the rootlist row in the store BEFORE it returns, so the page we navigate to is a real 0-track owner
/// page rendered from the store — not a skeleton waiting on an ack. The five call sites this replaced all awaited the
/// round trip first, which is why creating a playlist used to feel like a network operation.</para>
///
/// <para><b>The failure is observed, never awaited.</b> <see cref="PlaylistCreated.Completion"/> faults with a typed
/// <see cref="PlaylistMutationException"/> when the create dead-letters (the store has already rolled the optimistic row
/// back). We then mark the uri failed — which is what turns the open page's notice strip into
/// <c>DetailNotice.CreateFailed</c> — and offer <b>Retry</b>, which re-runs the whole flow with a NEW client-minted id
/// under the SAME name (a rejected id is never reused).</para>
/// </summary>
static class PlaylistCreateFlow
{
    /// <summary>Create a playlist at <paramref name="placement"/> (default = top of the rootlist), optionally navigating
    /// to it immediately. Returns null when there is no library bridge, or when the seam refused synchronously.</summary>
    public static PlaylistCreated? Create(ActionServices s, RootlistPlacement placement, bool navigate)
        => Create(s, placement, navigate, out _);

    /// <summary>The same create, also handing back the NAME it minted — every caller's toast needs it, and re-deriving
    /// it afterwards would race the very row this just added.</summary>
    public static PlaylistCreated? Create(ActionServices s, RootlistPlacement placement, bool navigate, out string name)
    {
        name = Menus.NextPlaylistName(s);
        return Start(s, name, placement, navigate);
    }

    static PlaylistCreated? Start(ActionServices s, string name, RootlistPlacement placement, bool navigate)
    {
        if (s.Library is not { } lib) return null;
        PlaylistCreated created;
        try { created = lib.CreatePlaylist(name, placement); }
        catch (Exception ex) { PlaylistEditErrors.Toast(ex); return null; }

        if (navigate)
        {
            // Arm the one-shot BEFORE the navigation so the page's first layout pass finds it: a brand-new playlist
            // needs a name, and the title editor is the only place to give it one.
            PlaylistCreateIntent.Arm(created.Uri);
            s.Go?.Invoke("pl:" + created.Uri, name);
        }
        Observe(s, lib, created, name, placement, navigate);
        return created;
    }

    static void Observe(ActionServices s, LibraryBridge lib, PlaylistCreated created, string name,
                        RootlistPlacement placement, bool navigate)
    {
        string uri = created.Uri;
        var completion = created.Completion;
        _ = Run();

        async Task Run()
        {
            try { await completion.ConfigureAwait(false); }
            catch (Exception ex)
            {
                Post(s, () => Failed(s, lib, uri, name, placement, navigate, ex));
                return;
            }
            Post(s, () =>
            {
                lib.SettleCreate(uri, ok: true);
                if (Announcer.IsAvailable) Announcer.Say(Loc.Get(Strings.Detail.NewPlaylist) + ": " + name);
            });
        }
    }

    static void Failed(ActionServices s, LibraryBridge lib, string uri, string name, RootlistPlacement placement,
                       bool navigate, Exception ex)
    {
        // The page (if it is open) learns through the bridge, not through this toast: a notice strip survives the
        // toast's 4 seconds and is the only thing still on screen when the user comes back to the tab.
        lib.SettleCreate(uri, ok: false);
        _ = ex;   // the KIND is not what distinguishes a failed create — there is one sentence and one recovery.
        Toast.Show(Loc.Get(Strings.Detail.Edit.CreateFailed), new ToastOptions
        {
            Severity = InfoBarSeverity.Error,
            ActionLabel = Loc.Get(Strings.Common.Retry),
            OnAction = () => Start(s, name, placement, navigate),
        });
        if (Announcer.IsAvailable) Announcer.Say(Loc.Get(Strings.Detail.Edit.CreateFailed), assertive: true);
    }

    static void Post(ActionServices s, Action a)
    {
        var post = s.Post;
        if (post is not null) post(a); else a();
    }
}

/// <summary>
/// A ONE-SHOT "this playlist was just created here" ticket, keyed by uri. <c>PlaylistCreateFlow</c> arms it at the
/// navigation edge; the detail page's title editor takes it in its first layout effect and opens in edit mode.
///
/// <para>A ticket rather than a model field because the two surfaces are a navigation apart: the model the page loads
/// comes from the store (which knows nothing about who navigated), and a prop would freeze at mount on a page that is
/// re-used across playlists. <see cref="Take"/> consumes, so a back-navigation to the same playlist does not re-open the
/// editor — the intent belonged to that one create, not to the uri forever.</para>
/// </summary>
static class PlaylistCreateIntent
{
    static readonly HashSet<string> Armed = new(StringComparer.Ordinal);

    public static void Arm(string uri)
    {
        if (uri.Length > 0) Armed.Add(uri);
    }

    /// <summary>True exactly once per <see cref="Arm"/>.</summary>
    public static bool Take(string uri) => uri.Length > 0 && Armed.Remove(uri);
}
