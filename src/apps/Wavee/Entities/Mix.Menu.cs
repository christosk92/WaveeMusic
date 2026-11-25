// ── Entities/Mix.Menu.cs — #48 Mix, the row menu's "Don't mix into the next song" (plan §4: the smallest complete cut) ────
//
// One toggle in a track row's menu, inside a playlist Spotify has mixed: it writes the local NoMix override for the pair
// (this row → the row after it in the playlist's order), which the planner honours (Playback.Mix.cs: the pair gets the base
// hand-off, the user's Crossfade or gapless) and which the Transitions view and the seams draw as a break. Spotify has no
// per-pair opt-out (decision D16), so the record lives on this PC only and says so ("Saved on this PC only" in the label's
// tooltip line of the Transitions view). Checked while the pair is opted out; running it again removes the record.

using FluentGpu.Controls;
using FluentGpu.Localization;

namespace Wavee;

public static class MixMenu
{
    /// <summary>The pair a row menu acts on: the hosting mixed playlist, its uri, and the two item ids (from → to). False
    /// when the row is not the single track of a mixed playlist, is the last row, or either side cannot mix.</summary>
    public static bool TryPair(in ActionContext c, out string playlistUri, out ulong fromItem, out ulong toItem)
    {
        playlistUri = "";
        fromItem = toItem = 0;
        var host = c.Target.Host;
        if (!host.IsSome || c.Target.Count != 1 || host.Rows is not { Count: 1 }) return false;
        var p = Entities.Playlist(host.Playlist);
        if (!p.IsValid || !p.MixOn) return false;
        int i = host.Rows[0];
        var edges = Entities.Current.Edges.PlaylistTracks;
        var targets = edges.Targets(p.Slot);
        var payload = edges.Payload(p.Slot);
        if ((uint)i >= (uint)targets.Length || i + 1 >= targets.Length) return false;
        if (payload[i].Kind != PlaylistItemKind.Track || payload[i + 1].Kind != PlaylistItemKind.Track) return false;
        if (((payload[i].Flags | payload[i + 1].Flags) & PlaylistItemFlags.NotMixable) != 0) return false;
        if (new Track(targets[i]).IsLocal || new Track(targets[i + 1]).IsLocal) return false;
        fromItem = MixRecipe.Hex64(Entities.Strings.Resolve(payload[i].ItemId));
        toItem = MixRecipe.Hex64(Entities.Strings.Resolve(payload[i + 1].ItemId));
        playlistUri = p.Id.Text;
        return fromItem != 0 && toItem != 0;
    }

    /// <summary>The meta line's Mix words: "Mixed", or "Mixed · 2 of 9 transitions edited" when some pairs are edited in Spotify
    /// or on this PC; null while Mix is off for the playlist (or this PC listens without it). Counts adjacent mixable pairs and
    /// the stored and local records over them: no analysis needed. UI thread, at the page's render.</summary>
    public static string? MetaSuffix(Playlist p)
    {
        if (!p.IsValid || !p.MixOn) return null;
        if (MixListenWithout.Is(p)) return null;
        var edges = Entities.Current.Edges.PlaylistTracks;
        var targets = edges.Targets(p.Slot);
        var payload = edges.Payload(p.Slot);
        string uri = p.Id.Text;
        int pairs = 0, edited = 0;
        for (int i = 0; i + 1 < targets.Length; i++)
        {
            if (payload[i].Kind != PlaylistItemKind.Track || payload[i + 1].Kind != PlaylistItemKind.Track) continue;
            if (((payload[i].Flags | payload[i + 1].Flags) & PlaylistItemFlags.NotMixable) != 0) continue;
            if (new Track(targets[i]).IsLocal || new Track(targets[i + 1]).IsLocal) continue;
            pairs++;
            ulong from = MixRecipe.Hex64(Entities.Strings.Resolve(payload[i].ItemId));
            ulong to = MixRecipe.Hex64(Entities.Strings.Resolve(payload[i + 1].ItemId));
            if (MixOverrides.TryGet(uri, from, to, out _) || MixData.TryGet(p.Slot, from, out _)) edited++;
        }
        return edited == 0 ? Loc.Get(Strings.Mix.MixedLine)
            : Strings.Mix.MixedLineEdited(edited, pairs);
    }

    /// <summary>The list bar offers the Mix toggle: the playlist is mixed, or the user may mix it (owner, collaborator) and Spotify says it may be.</summary>
    public static bool Offered(Playlist p)
        => p.IsValid && (p.MixOn || (Spotify.Mixes.CanWrite(p) && (p.MixFlags & PlaylistMixFlags.XmMixable) != 0));

    /// <summary>The toggle is lit: Mix is on and this PC plays it mixed.</summary>
    public static bool Lit(Playlist p) => p.IsValid && p.MixOn && !MixListenWithout.Is(p);

    /// <summary>The toggle (decision D3): a mixed playlist flips "listen without Mix"; an unmixed one the user may mix (owner or
    /// collaborator) turns Mix on at Spotify.</summary>
    public static void Toggle(Playlist p)
    {
        if (!p.IsValid) return;
        if (p.MixOn) { Spotify.Mixes.SetListenWithoutMix(p, without: !MixListenWithout.Is(p)); return; }
        if (Spotify.Mixes.CanWrite(p)) TurnOn(p);
    }

    /// <summary>Mix on at Spotify; only a failure says anything (the button lights when the reply lands).</summary>
    static void TurnOn(Playlist p)
        => Spotify.Mixes.TurnOn(p, outcome =>
        {
            if (outcome == Spotify.Mixes.Outcome.Landed) return;
            Notify.Say(Loc.Get(outcome == Spotify.Mixes.Outcome.Offline ? Strings.Mix.OnOffline : Strings.Mix.OnFailed),
                InfoBarSeverity.Warning, dedupeKey: "mix.state");
        });

    /// <summary>"Turn off Mix for everyone" (owner, collaborators): the write goes at once — the list reads unmixed while it is out —
    /// and the confirmation is the toast after it, with "Turn on again" as its undo (the owner's rule: inline over dialogs; nothing is
    /// lost either way, Spotify keeps every transition through off and on). A refusal puts Mix back on and says so.</summary>
    public static void TurnOffForEveryone(Playlist p)
        => Spotify.Mixes.TurnOff(p, outcome =>
        {
            switch (outcome)
            {
                case Spotify.Mixes.Outcome.Landed:
                    Notify.Say(Loc.Get(Strings.Mix.OffForEveryone), InfoBarSeverity.Success, actionLabel: Loc.Get(Strings.Mix.TurnOnAgain),
                        onAction: () => TurnOn(p), dedupeKey: "mix.state");
                    break;
                case Spotify.Mixes.Outcome.Offline:
                    Notify.Say(Loc.Get(Strings.Mix.OffOffline), InfoBarSeverity.Warning, dedupeKey: "mix.state");
                    break;
                case Spotify.Mixes.Outcome.Failed:
                    Notify.Say(Loc.Get(Strings.Mix.OffFailed), InfoBarSeverity.Warning, dedupeKey: "mix.state");
                    break;
            }
        });

    /// <summary>The list bar's Mix menu (UX direction rev 2): the transitions view, listen without Mix, turn Mix off for everyone
    /// (owner and collaborators only: <see cref="Spotify.Mixes.CanWrite"/>; live when the server offers the mix-state signal), the
    /// transitions view option (on hover · always) and "Mix when shuffling".</summary>
    public static IReadOnlyList<FluentGpu.Controls.MenuFlyoutItem> Items(Playlist p, Action toggleTransitions, bool transitionsOpen)
    {
        var items = new List<FluentGpu.Controls.MenuFlyoutItem>(8);
        if (p.MixOn) items.Add(FluentGpu.Controls.MenuFlyoutItem.Toggle(Loc.Get(Strings.Mix.Transitions), transitionsOpen, toggleTransitions));
        if (p.MixOn)
        {
            bool without = MixListenWithout.Is(p);
            items.Add(FluentGpu.Controls.MenuFlyoutItem.Toggle(Loc.Get(Strings.Mix.ListenWithout), without,
                () => Spotify.Mixes.SetListenWithoutMix(p, !without)));
            if (Spotify.Mixes.CanWrite(p))
                items.Add(new FluentGpu.Controls.MenuFlyoutItem(Loc.Get(Strings.Mix.TurnOffForEveryone), default, Spotify.Mixes.CanToggle(p),
                    () => TurnOffForEveryone(p)));
        }
        items.Add(FluentGpu.Controls.MenuFlyoutItem.Header(Loc.Get(Strings.Mix.ShowTransitions)));
        bool always = MixPrefs.ShowAlways;
        items.Add(FluentGpu.Controls.MenuFlyoutItem.RadioItem(Loc.Get(Strings.Mix.OnHover), !always, () => MixPrefs.SetShowAlways(false)));
        items.Add(FluentGpu.Controls.MenuFlyoutItem.RadioItem(Loc.Get(Strings.Mix.Always), always, () => MixPrefs.SetShowAlways(true)));
        bool shuffling = MixPrefs.WhenShuffling;
        items.Add(FluentGpu.Controls.MenuFlyoutItem.Toggle(Loc.Get(Strings.Mix.WhenShuffling), shuffling, () => MixPrefs.SetWhenShuffling(!shuffling)));
        return items;
    }

    /// <summary>Is this row's pair opted out on this PC?</summary>
    public static bool IsOptedOut(in ActionContext c)
        => TryPair(in c, out var uri, out var from, out var to)
           && MixOverrides.TryGet(uri, from, to, out var o) && o.Kind == MixOverrideKind.NoMix;

    /// <summary>The playlist row's version moves, so its page (the meta line, the marks) re-reads the overrides.</summary>
    static void Bump(in ActionContext c)
    {
        var p = Entities.Playlist(c.Target.Host.Playlist);
        if (p.IsValid) Entities.Current.Playlists.Bump(p.Slot);
    }

    /// <summary>Registered with the track verbs (Track.Menu.cs's InstallActions).</summary>
    public static void Register()
    {
        AppActions.Register(new AppAction
        {
            Id = ActionId.DontMixIntoNext, IconKey = ActionIcons.PlayNext,
            Label = static _ => Loc.Get(Strings.Mix.DontMixIntoNext),
            IsChecked = static c => IsOptedOut(in c),
            IsEnabled = static c => TryPair(in c, out _, out _, out _),
            Execute = static c =>
            {
                if (!TryPair(in c, out var uri, out var from, out var to)) return;
                if (MixOverrides.TryGet(uri, from, to, out var o) && o.Kind == MixOverrideKind.NoMix)
                {
                    MixOverrides.Clear(uri, from, to);
                    Log.Info("mix", "pair opt-out cleared");
                    Bump(in c);
                }
                else
                {
                    MixOverrides.Set(uri, from, to, MixOverrideKind.NoMix);
                    Log.Info("mix", "pair opted out on this PC");
                    Bump(in c);
                }
            },
        });
    }
}
