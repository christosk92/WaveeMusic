using System;
using System.Collections.Generic;
using System.Globalization;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using Wavee.Backend;
using Wavee.SpotifyLive.Audio;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The "who owns playback, and why does the bar say what it says" page (nav route <c>connect-diagnostics</c>).
///
/// <para>Renders THE authority (<see cref="Wavee.Backend.ConnectOwnership"/>) and its evidence verbatim: the current
/// <see cref="OwnerState"/>, the last folded cluster, the last put-state we sent and the server's answer to it, and the
/// local host's own clock — the exact inputs <c>PlaybackOwnership.OnCluster</c> folds and the exact outputs
/// <c>PlayerBar</c>/<c>PlaybackController</c> read. Nothing here is computed by the page; every row is a direct read of
/// state that already exists, which is why it can be re-read on every ownership/cluster/put-state change without side
/// effects (see the subscriptions in <see cref="Render"/>).</para></summary>
sealed class ConnectDiagnosticsPage : Component
{
    /// <summary>The nav route key. Registered in <c>ContentHost.PageFor</c>.</summary>
    public const string Route = "connect-diagnostics";

    const float ContentMaxW = 1000f;

    // Bumped by the subscriptions below (never written from render) — the one signal this page reads to know it must
    // re-render; the actual values come straight off Ownership/LastCluster/ConnectDiagnostics/Host on every render.
    readonly Signal<int> _tick = new(0);

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var post = UsePost();
        _ = svc?.Playback.AuthState.Value;   // re-render across login/logout so `connect` below re-resolves to the live session
        _ = _tick.Value;                      // re-render on every ownership / cluster / put-state change (see subscriptions)

        var connect = svc?.LiveHost?.Connect;
        var projection = connect?.Projection;
        var ownership = projection?.Ownership;
        // Keyed on the LIVE CONNECT instance: null while signed out, a fresh identity on every login/relogin — so the
        // subscriptions below re-attach exactly when the objects they point at do, instead of forever closing over
        // whatever was (or wasn't) live the first time this page happened to mount.
        var sessionKey = DepKey.FromRef(connect);

        UseEffect(() =>
        {
            if (ownership is null) return null;
            void OnOwnerChanged(OwnerTransition _) => post(() => _tick.Value = _tick.Peek() + 1);
            ownership.Changed += OnOwnerChanged;
            return () => ownership.Changed -= OnOwnerChanged;
        }, sessionKey);

        UseEffect(() =>
        {
            if (projection is null) return null;
            var sub = projection.Changes.Subscribe(_ => post(() => _tick.Value = _tick.Peek() + 1));
            return () => sub.Dispose();
        }, sessionKey);

        // Static/global — always available, independent of login state.
        UseEffect(() =>
        {
            void OnDiagChanged() => post(() => _tick.Value = _tick.Peek() + 1);
            ConnectDiagnostics.Changed += OnDiagChanged;
            return () => ConnectDiagnostics.Changed -= OnDiagChanged;
        });

        var body = new List<Element>(6)
        {
            ownership is not null ? OwnerCard(ownership.Current) : Card("Owner", Body("No live Connect session yet.")),
            projection?.LastCluster is { } cluster ? ClusterCard(cluster) : Card("Last cluster", Body("No cluster has folded yet.")),
            PutCard(ConnectDiagnostics.LastPut),
            EchoCard(ConnectDiagnostics.LastEcho),
            HostCard(connect?.Audio),
            Caption("This report is a direct read of the owner state, the last folded cluster and the last put-state "
                  + "round trip — reading it changes nothing. Attach it (or the log folder) to a bug report."),
        };

        return new BoxEl
        {
            Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
            Children =
            [
                PageHeader(),
                ScrollView(new BoxEl
                {
                    Direction = 1, Gap = 12f, MaxWidth = ContentMaxW, AlignSelf = FlexAlign.Stretch,
                    Padding = new Edges4(Spacing.L, Spacing.L, Spacing.L, Spacing.XXL),
                    Children = body.ToArray(),
                }) with { Grow = 1f, Shrink = 1f, MinHeight = 0f, ScrollKey = Route },
            ],
        };
    }

    // ── Sections ──────────────────────────────────────────────────────────────────────────────────

    static Element OwnerCard(OwnerState o) => Card("Owner",
        Row("State", o.Describe()),
        Row("Claim phase", o.Claim.ToString()),
        Row("Claim id", o.ClaimId > 0 ? o.ClaimId.ToString(CultureInfo.InvariantCulture) : "—"),
        Row("Started at", StampMs(o.ClaimStartedAtMs)),
        Row("Fence server ts", StampMs(o.FenceServerTs)),
        Row("Last server ts", StampMs(o.LastServerTs)),
        Row("Last seen (unconfirmed)", o.LastSeenActive.Length > 0
            ? o.LastSeenActive + " @ " + StampMs(o.LastSeenServerTs)
            : "—"));

    static Element ClusterCard(ClusterDelta c) => Card("Last cluster",
        Row("Active id (raw)", c.ActiveDeviceId.Length > 0 ? c.ActiveDeviceId : "—"),
        Row("Origin", c.PutMsgId != 0 ? c.Origin + " #" + c.PutMsgId.ToString(CultureInfo.InvariantCulture) : c.Origin.ToString()),
        Row("Update reason", c.UpdateReason.ToString(CultureInfo.InvariantCulture)),
        Row("Changed devices", c.ChangedDevices is { Count: > 0 } cd ? string.Join(", ", cd) : "—"),
        Row("Server ts", StampMs(c.ServerTimestampMs)),
        Row("Track uri", c.HasTrack ? c.Track.Uri : "—"),
        Row("Playing / paused", (c.IsPlaying ? "playing" : "not playing") + " / " + (c.IsPaused ? "paused" : "not paused")),
        Row("Position", c.PositionAsOfMs.ToString(CultureInfo.InvariantCulture) + " ms"));

    static Element PutCard(PutTrace p) => Card("Last put-state",
        Row("Msg id", p.MsgId == 0 ? "—" : p.MsgId.ToString(CultureInfo.InvariantCulture)),
        Row("Reason", p.Reason),
        Row("is_active", p.IsActive.ToString()),
        Row("started_playing_at", StampMs(p.StartedPlayingAtMs)),
        Row("has_been_playing_for", p.HasBeenPlayingForMs.ToString(CultureInfo.InvariantCulture) + " ms"),
        Row("Sent at", StampMs(p.AtMs)));

    static Element EchoCard(EchoTrace e) => Card("Last put-state response",
        Row("Msg id", e.MsgId == 0 ? "—" : e.MsgId.ToString(CultureInfo.InvariantCulture)),
        Row("Active id", e.ActiveId.Length > 0 ? e.ActiveId : "—"),
        Row("Server ts", StampMs(e.ServerTs)),
        Row("Cluster started_at", StampMs(e.ClusterStartedAtMs)),
        Row("Adopted", e.Adopted.ToString()),
        Row("Received at", StampMs(e.AtMs)));

    static Element HostCard(AudioPlaybackStack? audio)
    {
        if (audio is null)
            return Card("Host", Body("No local audio stack — a pure Connect viewer, or nothing has loaded yet."));
        var host = audio.Host;
        return Card("Host",
            Row("Playing", host.IsPlaying.ToString()),
            Row("Clock valid", host.ClockValid.ToString()),
            Row("Position", host.PositionMs.ToString(CultureInfo.InvariantCulture) + " ms"));
    }

    static string StampMs(long ms) => ms <= 0 ? "—"
        : DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    // ── Chrome (modeled on PlaybackRuntimeDiagnosticsPage's Card/Row/Body layout) ────────────────────

    static Element PageHeader() => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
        Padding = new Edges4(Spacing.L, Spacing.L, Spacing.L, Spacing.M),
        Children =
        [
            Icon(Icons.Devices, 22f, Tok.TextPrimary),
            WaveeType.PageHero("Connect diagnostics") with { Grow = 1f },
        ],
    };

    static Element Card(string title, params Element[] rows)
    {
        var kids = new List<Element>(rows.Length + 1)
        {
            new TextEl(title) { Size = 12f, Weight = 600, Color = Tok.TextSecondary },
        };
        kids.AddRange(rows);
        return new BoxEl
        {
            Direction = 1, Gap = 6f, Padding = Edges4.All(12f),
            Fill = Tok.FillLayerAlt, Corners = CornerRadius4.All(Radii.Control),
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children = kids.ToArray(),
        };
    }

    static Element Row(string label, string? value) => new BoxEl
    {
        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Start,
        Children =
        [
            new TextEl(label) { Size = 12f, Color = Tok.TextSecondary, Width = 160f, Shrink = 0f },
            new TextEl(string.IsNullOrWhiteSpace(value) ? "—" : value)
                { Size = 12f, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, Grow = 1f },
        ],
    };

    static Element Body(string text) => new TextEl(text) { Size = 12f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap };

    static Element Caption(string text) => new TextEl(text) { Size = 11f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap };
}
