// ── Shell/Shell.Links.cs — THE link door: what clicking a link in content does ─────────────────────────────────────
//
// Role: SHELL. Every link a user clicks inside the app (a notification row, a description's <a href>, an artist's
// social link, a release-notes link, a merch row) comes here and is resolved ONCE: a Spotify entity (`spotify:` uri or
// an `open.spotify.com` web link, any locale segment, any `?si=`) opens its in-app page through the same deep-link
// parser an OS activation takes (`Shell.DeepLink`), a track plays (it has no page), and only a link that addresses no
// in-app destination goes to the browser. Before this door the notification panel tested `StartsWith("http")` and sent
// every web link to the browser, so `https://open.spotify.com/episode/…` left the app; rich-text anchors never routed
// at all (their route seam was never installed). A link is UNTRUSTED content: of the deep-link verbs only an OPEN and a
// track PLAY are honoured here — a `wavee://quit` / `pause` / `diag` in a playlist description must never drive the app.

using FluentGpu.Hooks;

namespace Wavee;

public static partial class Shell
{
    /// <summary>What a link click does.</summary>
    public enum LinkAction : byte { None, Open, Play, Browser }

    /// <summary>A resolved link: <see cref="Route"/> for an <see cref="LinkAction.Open"/>, <see cref="Target"/> the track uri
    /// for a <see cref="LinkAction.Play"/> or the url for a <see cref="LinkAction.Browser"/>.</summary>
    public readonly record struct LinkTarget(LinkAction Action, Route Route, string Target);

    /// <summary>THE resolver: is this a Spotify entity (→ its in-app route, or a play for a track) or an external web page
    /// (→ the browser)? Anything else (a non-web scheme, a deep-link verb other than open/play, garbage) is refused.</summary>
    public static LinkTarget ResolveLink(string? link)
    {
        if (string.IsNullOrWhiteSpace(link)) return new LinkTarget(LinkAction.None, Route.None, "");
        var verb = DeepLink(link, Platform.Settings.Get(Platform.Keys.DeveloperMode));
        if (verb.Kind == DeepLinkKind.Open) return new LinkTarget(LinkAction.Open, verb.Route, "");
        if (verb.Kind == DeepLinkKind.Play && verb.Context.Length > 0) return new LinkTarget(LinkAction.Play, Route.None, verb.Context);
        string url = link.Trim();
        return Actions.PlayLinkRules.IsWebUrl(url)
            ? new LinkTarget(LinkAction.Browser, Route.None, url)
            : new LinkTarget(LinkAction.None, Route.None, "");
    }

    /// <summary>The route key a link opens in-app, or null — the rich-text route seam (<see cref="Controls.RouteForUri"/>).</summary>
    public static string? RouteKeyOfLink(string? link)
        => ResolveLink(link) is { Action: LinkAction.Open } t ? NameOf(t.Route) : null;

    /// <summary>The rich-text anchors' two seams, once at boot (App.cs): a link with a route goes to the anchor's own
    /// navigation, anything else the door can follow (a track, a web page) to <see cref="OpenLink"/>.</summary>
    public static void InstallLinkSeams()
    {
        Controls.RouteForUri = static link => RouteKeyOfLink(link);
        Controls.LinkOpener = static link => ResolveLink(link).Action == LinkAction.None ? null : () => OpenLink(link);
    }

    /// <summary>Follow a link the user clicked: in-app when it addresses a destination, else the browser. False when the
    /// link was refused (nothing happened).</summary>
    public static bool OpenLink(string? link)
    {
        var t = ResolveLink(link);
        switch (t.Action)
        {
            case LinkAction.Open:
                Log.Info("nav", "link.open in-app route=" + NameOf(t.Route));
                GoToLinked(t.Route);
                return true;
            case LinkAction.Play:
                Log.Info("nav", "link.open play " + t.Target);
                OnPlayContext?.Invoke(EntityUri.Parse(t.Target));
                return true;
            case LinkAction.Browser:
                OpenBrowser(t.Target);
                return true;
            default:
                Log.Info("nav", "link.refused");
                return false;
        }
    }

    /// <summary>Open an http(s) url in the user's browser — the ONE external opener for a link (the host's
    /// <c>OpenExternal</c> seam, else the OS hook). A non-web scheme never reaches the shell: a decoded url must never
    /// launch a program. Use <see cref="OpenLink"/> for anything a user clicked in content; this is for a page the app
    /// itself chose to open in the browser ("Open in browser").</summary>
    public static void OpenBrowser(string? url)
    {
        if (!Actions.PlayLinkRules.IsWebUrl(url)) return;
        string u = url!.Trim();
        Log.Info("nav", "link.open browser host=" + (Uri.TryCreate(u, UriKind.Absolute, out var parsed) ? parsed.Host : "?"));
        if (Actions.Services.OpenExternal is { } open) { open(u); return; }
        InputHooks.Current.Default.OpenUri?.Invoke(u);
    }

    /// <summary>An OPEN verb's arrival, shared by the activation door (<see cref="ApplyDeepLink"/>) and a link click.
    /// <c>route=settings&amp;arg=&lt;tab&gt;</c>: the arg is a TAB SLUG, never a place of its own (D4) — it is handed to
    /// Settings, not committed as a route with an Arg (<c>Dest</c> would title the tab with it, <c>SameSlot</c> would
    /// swallow a re-open of the same page on another tab).</summary>
    static void GoToLinked(in Route route)
    {
        if (route.Kind == RouteKind.Settings && ArgOf(route) is { } slug && OnSettingsTabRequested is { } openTab)
        {
            openTab(slug);
            return;
        }
        GoTo(route);
    }
}
