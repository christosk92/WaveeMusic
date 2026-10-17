// ── Platform/Modules.UI.cs ─────────────────────────────────────────────────────────────────────────────────────────
// the module surfaces' install, the play verbs and the page's core facts
//
// Role: Host
// Owner: T
// Wave: 6
// Spec: ch 09 §9.5 (the module page), ch 24 (the watch page's stage), gap register G-021 · G-151 · G-212; themes plan T10b3
//
// THE ONE PAGE A PLAYBACK MODULE DESCRIBES is drawn by `Themes\Common\CommonModulePage.cs` (the registered `Pages.Module` view) and mounted by
// `Modules.PageHost` (`Modules.Page.cs`). Every DECISION is `Modules.cs`'s (the route grammar, the three templates, the watch projection, the stage
// playable, the shared item budget); this file keeps the install, the verbs a page forwards to (play, toggle, open a url, navigate, a document
// action) and the facts the host and the view read (the seed document, the load, what is playing).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Sdk;

namespace Wavee;

public static partial class Modules
{
    // ══ 0. THE INSTALL ═════════════════════════════════════════════════════════════════════════════════════════════

    static int s_uiInstalled;

    /// <summary>The Wave-6 composition of the module surfaces (called by App.cs after <see cref="Boot"/>): the
    /// <c>module:</c> page, the two audio seams (G-110), the link router (G-021) and the module video tier (G-148).
    /// Idempotent; every seam is an assignment, so a second call changes nothing.</summary>
    public static void InstallUi()
    {
        if (Interlocked.Exchange(ref s_uiInstalled, 1) != 0) return;
        Shell.SetPage(Shell.RouteKind.Module, static (in Shell.Route route) => PageFor(in route));
        Playback.Audio.ModuleOpen = OpenAudio;
        Playback.Audio.LocalPath = Playlist.LocalPathOf;
        PlayLinkRouter.Modules = LinkModulesNow;
        PlayLinkRouter.Match = MatchLinkAsync;
        InstallVideoTier();
    }

    /// <summary>THE <c>PlayLinkRouter.Modules</c> seam: the match-capable installed modules, in catalog order, labelled by
    /// their own manifests (an authored loc key wins, then the authored label, then the display name + ellipsis).</summary>
    public static IReadOnlyList<LinkModule> LinkModulesNow()
    {
        if (s_host is not { } host) return [];
        var rows = new List<LinkModule>(host.Installed.Count);
        string fallbackPlaceholder = Loc.Get(Strings.Play.Placeholder);
        foreach (InstalledModule module in host.Installed)
        {
            ModuleManifest m = module.Manifest;
            if (!ModuleCapabilities.Declares(m, ModuleCapabilities.Match)) continue;
            string? authored = m.Menu?.LabelLocKey is { Length: > 0 } key && global::FluentGpu.Localization.Localization.Has(key) ? Loc.Get(key) : m.Menu?.Label;
            rows.Add(new LinkModule(module.Id, Actions.PlayLinkRules.MenuLabel(authored, m.DisplayName, module.Id),
                Actions.PlayLinkRules.PlaceholderFor(m.Menu?.Placeholder, fallbackPlaceholder)));
        }
        return rows;
    }


    // ══ 1. THE PLAY VERBS ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Play a module playable by uri from a page: the cached answer plays at once; otherwise it resolves on the
    /// pool and plays on the UI thread. A failure is the shared play-failure toast with the module's own words.</summary>
    public static void PlayUri(string playableUri)
    {
        if (s_host is not { } host || playableUri.Length == 0) return;
        if (Playables.Get(playableUri) is { } cached) { Play(playableUri, cached); return; }
        _ = Task.Run(async () =>
        {
            try
            {
                ResolvedPlayable resolved = await host.ResolveAsync(playableUri, force: false, CancellationToken.None).ConfigureAwait(false);
                Playback.ToUi(() => Play(playableUri, resolved));
            }
            catch (Exception ex)
            {
                if (Actions.PlayLinkRules.IsCancelled(ex)) return;
                Log.Info("module", "play failed: " + ex.Message);
                Playback.ToUi(() => Notify.Say(Actions.PlayLinkRules.ErrorText(ex, Loc.Get(Strings.Play.Failed)), InfoBarSeverity.Error,
                    Loc.Get(Strings.Play.TryAgain), () => PlayUri(playableUri), Actions.PlayLinkRules.FailureToastKey));
            }
        });
    }

    /// <summary>The stage and the play capsule: the playable in the bar toggles, anything else plays.</summary>
    public static void PlayOrToggle(string playableUri)
    {
        EntityId now = Playback.CurrentId.Peek();
        if (!now.IsEmpty && now.Provider == EntityProvider.Module && string.Equals(now.Text, playableUri, StringComparison.Ordinal))
            Playback.TogglePlay("module.play");
        else PlayUri(playableUri);
    }

    /// <summary>Open a web url in the user's browser — only an http(s) url with a host ever reaches the shell.</summary>
    internal static void OpenUrl(string? url)
    {
        if (!Actions.PlayLinkRules.IsWebUrl(url)) return;
        if (Actions.Services.OpenExternal is { } open) { open(url!); return; }
        InputHooks.Current.Default.OpenUri?.Invoke(url!);
    }

    internal static void Navigate(string routeKey, string? label) => Shell.GoTo(Shell.Parse(routeKey, label ?? ""));

    /// <summary>What a document action DOES, or null when this build cannot honour it (the control is then ABSENT,
    /// never dead): play through the resolve path, open an http(s) url, or send the id back over module/action.</summary>
    internal static Action? InvokeFor(string moduleId, string kind, string? id, string? playableId, string? url) => kind switch
    {
        PageAction.KindPlay => playableId is { Length: > 0 } pid ? () => PlayOrToggle(ModuleUri.Encode(moduleId, pid)) : null,
        PageAction.KindOpenUrl => Actions.PlayLinkRules.IsWebUrl(url) ? () => OpenUrl(url) : null,
        PageAction.KindModuleAction => id is { Length: > 0 } actionId ? () => InvokeAction(moduleId, actionId) : null,
        _ => null,
    };


    // ══ 2. THE PAGE'S CORE FACTS ═══════════════════════════════════════════════════════════════════════════════════

    // Figure spaces (U+2007) MEASURE like digits and paint nothing, so the derived shimmer gets a title bar and two
    // shorter lines at believable widths with no string that could flash as real copy.
    static readonly ModulePageDoc Seed = new(ModulePageDoc.CurrentVersion, ModulePageDoc.TemplateEntity,
        new PageHero(new string(' ', 12), null, new string(' ', 8), null, new string(' ', 5), false), [], [], null);

    static Task<ModulePageDoc> LoadPage(string pageUri, CancellationToken ct)
        => s_host is { } host
            ? host.PageAsync(pageUri, ct)
            : Task.FromException<ModulePageDoc>(new ModuleException(ModuleErrorCode.Unsupported, "No module host is running."));

    /// <summary>The playing PLAYABLE uri when it is a module's, else "" (subscribes).</summary>
    internal static string PlayingModuleUri()
    {
        EntityId id = Playback.CurrentId.Value;
        return !id.IsEmpty && id.Provider == EntityProvider.Module ? id.Text : "";
    }
}
