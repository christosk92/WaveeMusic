// ── Shell/Shell.Drop.cs ───────────────────────────────────────────────────────────────────────────────────────────────
// DROP ANYWHERE: the window's catch-all, the drop panel's state and targets, and the precise targets' OS arms
// (plan 240 §3.15, D15, D16; UX S12; board frames 22-24)
//
// Role: CORE (targets and signals; the panel's picture is the theme's `Shell.FileDrop` view)
// Spec: handoff/request-plans/240-local-playlist-files-implementation.md §3.15; 240-local-files-ux.md S12.
//
// The window keeps ONE catch-all target over everything (`Window`, on the frame's ground). It and the panel's own targets
// show the drop panel while the pointer is over them: two to four large zones that fit what is dragged (`DropPlan.Verbs`).
// Precise targets sit above it and win by the engine's nearest-target rule: the queue (its drop line, `Reorderable`'s
// `alsoAccepts`), the player bar (Play now), a sidebar playlist (links now; local files refused with the D12 sentence). Over
// one of those the panel fades out. A payload nothing can use is refused by the catch-all with the plan's reason, so the chip
// says why and nothing dims.
//
// Allocation: the plan is built on the first ask of a drag (cold); OnOver, CanAccept and the captions read cached fields only.

using FluentGpu.Controls;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;

namespace Wavee;

public static partial class DropAnywhere
{
    /// <summary>Every OS kind a drop target here takes, in the order a multi-format drag should be read: files, then links, then text.</summary>
    public static readonly string[] OsKinds = [DropKinds.Files, DropKinds.Uri, DropKinds.Text];

    /// <summary>The most zones a plan offers (folders: Play · Add as a music folder · Add to queue; the fourth is spare).</summary>
    public const int MaxZones = 4;

    // ── the panel's state ─────────────────────────────────────────────────────────────────────────────────────────────

    const int OverNothing = 0, OverWindow = 1, OverPanel = 2, OverZone = 3;

    /// <summary>Which of the panel's surfaces the pointer is over: nothing, the window's ground, the panel card, or zone i
    /// (<see cref="OverZone"/> + i). Written on Enter/Leave edges only.</summary>
    static readonly Signal<int> s_over = new(OverNothing);

    static readonly Signal<bool> s_shown = new(false);
    static readonly Signal<int> s_hot = new(-1);
    static readonly Signal<DropZoneLook[]> s_zones = new([]);
    static DropPlan? s_armed;
    static string? s_defaultCaption;

    /// <summary>The panel shows while the pointer is over the ground, the card or a zone, for a plan it can use.</summary>
    public static IReadSignal<bool> Shown => s_shown;

    /// <summary>The zone under the pointer (-1: none).</summary>
    public static IReadSignal<int> Hot => s_hot;

    /// <summary>The live drag's zones (a new array once per drag: the panel re-renders on that edge only).</summary>
    public static IReadSignal<DropZoneLook[]> Zones => s_zones;

    /// <summary>The content region's centre relative to the window's (the panel centres on the content, board 22). Written by the
    /// shell when the region's bounds change.</summary>
    public static readonly Signal<Point2> CentreOffset = new(default);

    static void Over(int where)
    {
        s_over.Value = where;
        Publish();
    }

    static void Leave(int where)
    {
        if (s_over.Peek() != where) return;   // the next surface's Enter already ran
        s_over.Value = OverNothing;
        Publish();
    }

    static void Publish()
    {
        int over = s_over.Peek();
        bool shown = over != OverNothing && s_armed is { Usable: true };
        if (s_shown.Peek() != shown) s_shown.Value = shown;
        int hot = over >= OverZone ? over - OverZone : -1;
        if (s_hot.Peek() != hot) s_hot.Value = hot;
    }

    /// <summary>Make <paramref name="plan"/> the panel's (cold: once per drag): its zones and the catch-all's caption.</summary>
    static void Arm(DropPlan plan)
    {
        if (ReferenceEquals(plan, s_armed)) return;
        s_armed = plan;
        Shell.RefreshContentRegion();   // the panel centres on the content as it is now
        s_zones.Value = LooksOf(plan);
        s_defaultCaption = plan.Verbs.Length > 0 ? TitleOf(plan.Verbs[0]) : null;
    }

    /// <summary>The drag is over (dropped, cancelled, or it left the window): hide the panel.</summary>
    static void End()
    {
        s_refusingRow = false;
        s_over.Value = OverNothing;
        s_armed = null;
        Publish();
    }

    // ── the window's catch-all ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The frame ground's target: shows the panel; a drop that misses every zone does the plan's first verb (Play now,
    /// Play, Play as a queue, Open), which is what a drop anywhere did before the panel.</summary>
    public static readonly DropTargetSpec Window = new(OsKinds,
        OnEnter: static s => { Arm(PlanOf(s)); Over(OverWindow); s.Caption = s_defaultCaption; },
        OnOver: static s => s.Caption = s_defaultCaption,
        OnLeave: static _ => Leave(OverWindow),
        OnDrop: static s => DropDefault(s))
    {
        CanAccept = static s => PlanOf(s).Usable,
        RefusalCaption = static s => PlanOf(s).Refusal,
    };

    /// <summary>The panel card itself, between and around the zones: the same as the catch-all, and the spotlight's cut-out
    /// (the scrim dims the app, never the panel) while the panel shows.</summary>
    public static readonly DropTargetSpec Panel = new(OsKinds,
        OnEnter: static s => { Arm(PlanOf(s)); Over(OverPanel); s.Caption = s_defaultCaption; },
        OnOver: static s => s.Caption = s_defaultCaption,
        OnLeave: static _ => Leave(OverPanel),
        OnDrop: static s => DropDefault(s))
    {
        CanAccept = static s => PlanOf(s).Usable,
        VisualPolicy = DropTargetVisualPolicy.Spotlight,
        SpotlightWhen = static _ => s_shown.Peek(),
    };

    static void DropDefault(DragSession s)
    {
        var plan = PlanOf(s);
        End();
        if (plan.Verbs.Length > 0) Run(plan, plan.Verbs[0]);
        Forget();
    }

    static readonly DropTargetSpec[] s_zoneTargets = BuildZoneTargets();

    /// <summary>Zone <paramref name="index"/>'s target: it lights while the pointer is over it and runs its verb on a drop.</summary>
    public static DropTargetSpec ZoneTarget(int index) => s_zoneTargets[Math.Clamp(index, 0, MaxZones - 1)];

    static DropTargetSpec[] BuildZoneTargets()
    {
        var targets = new DropTargetSpec[MaxZones];
        for (int i = 0; i < MaxZones; i++)
        {
            int zone = i;
            targets[i] = new DropTargetSpec(OsKinds,
                OnEnter: s => { Arm(PlanOf(s)); Over(OverZone + zone); s.Caption = CaptionOf(zone); },
                OnOver: s => s.Caption = CaptionOf(zone),
                OnLeave: _ => Leave(OverZone + zone),
                OnDrop: s =>
                {
                    var plan = PlanOf(s);
                    var verbs = s_armed?.Verbs ?? plan.Verbs;
                    End();
                    if (zone < verbs.Length) Run(plan, verbs[zone]);
                    Forget();
                })
            {
                CanAccept = s => PlanOf(s) is { Usable: true } p && zone < p.Verbs.Length,
            };
        }
        return targets;
    }

    static string? CaptionOf(int zone) => s_armed is { } plan && zone < plan.Verbs.Length ? s_zoneCaptions[(int)plan.Verbs[zone]] : null;

    static readonly string?[] s_zoneCaptions = new string?[8];

    // ── the zones' words ──────────────────────────────────────────────────────────────────────────────────────────────

    static DropZoneLook[] LooksOf(DropPlan plan)
    {
        var looks = new DropZoneLook[Math.Min(plan.Verbs.Length, MaxZones)];
        for (int i = 0; i < looks.Length; i++)
        {
            DropVerb verb = plan.Verbs[i];
            s_zoneCaptions[(int)verb] = TitleOf(verb);
            looks[i] = new DropZoneLook(GlyphOf(verb), verb is DropVerb.PlayNext or DropVerb.AddToQueue ? WaveeIcons.Font : null, TitleOf(verb), DetailOf(plan, verb));
        }
        return looks;
    }

    static string TitleOf(DropVerb verb) => Loc.Get(verb switch
    {
        DropVerb.PlayNow => Strings.Drop.PlayNow,
        DropVerb.PlayNext => Strings.Drop.PlayNext,
        DropVerb.AddToQueue => Strings.Drop.AddToQueue,
        DropVerb.Play => Strings.Drop.Play,
        DropVerb.AddMusicFolder => Strings.Drop.AddMusicFolder,
        DropVerb.PlayAsQueue => Strings.Drop.PlayAsQueue,
        _ => Strings.Drop.Open,
    });

    static string GlyphOf(DropVerb verb) => verb switch
    {
        DropVerb.PlayNext => WaveeIcons.PlayNext,
        DropVerb.AddToQueue => WaveeIcons.PlayAfter,
        DropVerb.AddMusicFolder => Icons.FolderOpen,
        DropVerb.Open => Icons.OpenInNewWindow,
        _ => Icons.Play,
    };

    /// <summary>The zone's one-line consequence; a count reads the plan's count signal, so it lands when the listing does.</summary>
    static Func<string> DetailOf(DropPlan plan, DropVerb verb)
    {
        string fixedLine = verb switch
        {
            DropVerb.PlayNow => Loc.Get(Strings.Drop.PlayNowDetail),
            DropVerb.PlayNext => Loc.Get(Strings.Drop.PlayNextDetail),
            DropVerb.AddMusicFolder => Loc.Get(Strings.Drop.AddMusicFolderDetail),
            DropVerb.Open => Loc.Get(Strings.Drop.OpenDetail),
            _ => "",
        };
        if (fixedLine.Length > 0 && !(verb == DropVerb.PlayNow && plan.Kind == DropPayloadKind.Links && !plan.AllTracks)) return () => fixedLine;
        if (plan.Kind == DropPayloadKind.Links && !plan.AllTracks) { string line = Loc.Get(Strings.Drop.PlayNowDetail); return () => line; }
        bool afterQueued = verb == DropVerb.AddToQueue && plan.Kind == DropPayloadKind.Folders;
        return () =>
        {
            int songs = plan.Songs.Value;
            if (!plan.PathsKnown) return verb == DropVerb.AddToQueue ? Loc.Get(Strings.Drop.AfterQueued) : "";
            string words = songs < 0 ? Loc.Get(Strings.Drop.Counting) : SongWords(plan, songs);
            return afterQueued ? Strings.Drop.SongsAfterQueued(words) : words;
        };
    }

    // ── the chip ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The chip of an OS drag: the count and kind ("12 songs · 2 folders"), or nothing (the refusal glyph and reason
    /// ride the chip's caption). Reads cached words only: it runs inside the frame region.</summary>
    public static DragChipSpec? Chip(DragState state)
    {
        if (!state.Active || !(state.Offers(DropKinds.Files) || state.Offers(DropKinds.Uri) || state.Offers(DropKinds.Text))) return null;
        if (Current is not { } plan) return null;
        if (!plan.Usable) return new DragChipSpec(Title: plan.Refusal ?? "", Glyph: Icons.Cancel);
        // Over a playlist that refuses local files the caption carries the reason; the glyph says no.
        string glyph = s_refusingRow ? Icons.Cancel : plan.Kind switch
        {
            DropPayloadKind.Folders => Icons.Folder,
            DropPayloadKind.Links => Icons.Link,
            _ => Icons.MusicNote,
        };
        return new DragChipSpec(Title: plan.Chip, Glyph: glyph);
    }

    // ── the precise targets' OS arms ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The player bar: files and links play now (board 22 ②); folders and a playlist file are not its to take.</summary>
    public static readonly DropTargetSpec PlayerBar = new(OsKinds,
        OnEnter: static s => s.Caption = s_playNowCaption ??= Loc.Get(Strings.Drop.PlayNow),
        OnOver: static s => s.Caption = s_playNowCaption,
        OnDrop: static s =>
        {
            var plan = PlanOf(s);
            End();
            Deposit(plan, DropDeposit.PlayNow);
            Forget();
        })
    {
        CanAccept = static s => PlanOf(s).PlayerBarTakes,
        VisualPolicy = DropTargetVisualPolicy.Spotlight,
    };

    static string? s_playNowCaption;

    /// <summary>The queue's foreign gate for an OS payload (its <c>Reorderable</c> takes <see cref="OsKinds"/> too).</summary>
    public static bool QueueAccepts(object? payload) => IsOsPayload(payload) && PlanOfPayload(payload).QueueTakes;

    /// <summary>The queue's caption at its drop line ("3 songs land here").</summary>
    public static string? QueueCaption(object? payload) => IsOsPayload(payload) ? PlanOfPayload(payload).QueueCaption : null;

    /// <summary>A drop on the queue's line: the songs enter at queue-relative <paramref name="userIndex"/> (F9).</summary>
    public static void DropOnQueue(object? payload, int userIndex)
    {
        var plan = PlanOfPayload(payload);
        End();
        Deposit(plan, DropDeposit.At, userIndex);
        Forget();
    }

    /// <summary>A sidebar playlist row's OS arm, folded onto its resource target: Spotify track links add to the playlist now. Local
    /// files are refused there with the D12 sentence ("Adding local files to a Spotify playlist comes later", board 24 ③): the row
    /// takes the hover so the window's catch-all cannot swallow the refusal, sets the no-drop effect (the OS cursor refuses, nothing
    /// lights), puts the reason in the chip with the not-allowed glyph, and a drop there only says why. Rows that are not editable
    /// playlists stay transparent to OS drags.</summary>
    public static DropTargetSpec WithOsArm(DropTargetSpec inner, string? playlistUri, string? playlistName, Action<string[]> addLinks)
    {
        if (string.IsNullOrEmpty(playlistUri)) return inner;
        string caption = Drag.AddTo(playlistName ?? "");
        string[] kinds = [.. inner.AcceptKinds, .. OsKinds];
        static bool Os(DragSession s) => s.Kind is DropKinds.Files or DropKinds.Uri or DropKinds.Text;
        static bool Takes(DropPlan p) => p.Kind == DropPayloadKind.Links && p.AllTracks;
        void Hover(DragSession s)
        {
            var plan = PlanOf(s);
            if (Takes(plan)) { s.Caption = caption; return; }
            s_refusingRow = true;
            s.Caption = s_localLater ??= Loc.Get(Strings.Drop.LocalToSpotifyLater);
            s.Effect = DropEffect.None;
        }
        return inner with
        {
            AcceptKinds = kinds,
            OnEnter = s => { if (Os(s)) Hover(s); else inner.OnEnter?.Invoke(s); },
            OnOver = s => { if (Os(s)) Hover(s); else inner.OnOver?.Invoke(s); },
            OnLeave = s => { if (Os(s)) s_refusingRow = false; else inner.OnLeave?.Invoke(s); },
            OnDrop = s =>
            {
                if (!Os(s)) { inner.OnDrop?.Invoke(s); return; }
                var plan = PlanOf(s);
                End();
                if (Takes(plan)) addLinks(plan.Entities);
                else Notify.Say(s_localLater ??= Loc.Get(Strings.Drop.LocalToSpotifyLater), InfoBarSeverity.Informational);
                Forget();
            },
            CanAccept = s => Os(s) ? PlanOf(s) is { IsLocal: true } or { Kind: DropPayloadKind.Links, AllTracks: true } : inner.CanAccept?.Invoke(s) ?? true,
            RefusalCaption = s => Os(s) ? null : inner.RefusalCaption?.Invoke(s),
            Transparent = s => Os(s) ? PlanOf(s) is not ({ IsLocal: true } or { Kind: DropPayloadKind.Links, AllTracks: true })
                                     : inner.Transparent?.Invoke(s) ?? false,
            SpotlightWhen = s => Os(s) ? Takes(PlanOf(s)) : inner.SpotlightWhen?.Invoke(s) ?? true,
        };
    }

    /// <summary>The pointer is over a playlist row that refuses the live local-files drag (the chip says why).</summary>
    static bool s_refusingRow;

    static string? s_localLater;

    /// <summary>Dropped track links as the in-app payload a playlist deposit takes (the tracks resolve to their rows).</summary>
    public static DragPayload LinkPayload(string[] uris)
    {
        var tracks = new Track[uris.Length];
        for (int i = 0; i < uris.Length; i++) tracks[i] = global::Wavee.Entities.Track(EntityId.Parse(uris[i].AsSpan()));
        return new DragPayload(DragKind.Track, uris[0], uris[0], "", Tracks: tracks);
    }

    // ── the panel's props ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The props of the theme's drop panel (<see cref="ViewKeys.ShellFileDrop"/>), one cached record.</summary>
    public static readonly FileDropProps Props = new()
    {
        Over = s_shown,
        Zones = s_zones,
        Hot = s_hot,
        CentreOffset = CentreOffset,
        PanelTarget = Panel,
        ZoneTarget = ZoneTarget,
    };
}

public static partial class DropAnywhere
{
    // ── the drag a headless host or a live drive plays (no OLE source exists there) ───────────────────────────────────────

    /// <summary>One step of a played OS drag, through the engine's external-drop seam exactly as the OLE target forwards a real one
    /// (window DIP): enter with the payloads, move, leave, or drop (a file drop hands the list again, as the OLE target does). False
    /// when the host wired no seam.</summary>
    public static bool Play(string step, Point2 at, string[]? paths, string[]? uris, string? text, out string effect)
    {
        var hooks = FluentGpu.Hooks.InputHooks.Current.Default;
        effect = "none";
        switch (step)
        {
            case "enter":
            {
                if (hooks.ExternalDragEnter is not { } enter) return false;
                var offer = new ExternalDragOffer(paths is null ? null : new FileDropData(paths),
                    uris is { Length: > 0 } ? new UriDropData(uris) : null, string.IsNullOrEmpty(text) ? null : new TextDropData(text));
                effect = enter(at, offer, KeyModifiers.None).ToString();
                return true;
            }
            case "over":
                if (hooks.ExternalDragOver is not { } over) return false;
                effect = over(at, KeyModifiers.None).ToString();
                return true;
            case "leave":
                if (hooks.ExternalDragLeave is not { } leave) return false;
                leave();
                return true;
            case "drop":
            {
                bool accepted = paths is { Length: > 0 }
                    ? hooks.ExternalDropFiles?.Invoke(at, paths, KeyModifiers.None) ?? false
                    : hooks.ExternalDrop?.Invoke(at, KeyModifiers.None) ?? false;
                effect = accepted ? "accepted" : "refused";
                return hooks.ExternalDropFiles is not null;
            }
        }
        return false;
    }
}
