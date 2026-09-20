using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Backend.Audio;
using Wavee.SpotifyLive.Audio;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The "why is local playback not ready?" page (nav route <c>playback-diagnostics</c>).
///
/// <para>Renders the provisioner's own <see cref="PlaybackRuntimeDiagnostics"/> report verbatim: whether this build
/// even contains local-playback support, every directory a runtime was looked for (and whether the DLL/manifest were
/// there), how the search ended, and the verification result if one was reached. Everything here is data the
/// provisioner already computed — the page adds no detection logic of its own, which is why it can be re-read on a
/// button press without side effects.</para></summary>
sealed class PlaybackRuntimeDiagnosticsPage : Component
{
    /// <summary>The nav route key. Registered in <c>ContentHost.PageFor</c>; the label/glyph live in
    /// <c>ShellNav.Dest</c> (spelled there as a literal — that file is source-included by Wavee.Tests).</summary>
    public const string Route = "playback-diagnostics";

    const float ContentMaxW = 1000f;

    readonly Signal<int> _refresh = new(0);

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var hooks = UseContext(InputHooks.Current);
        _ = _refresh.Value;   // re-read the provisioner when "Refresh" is pressed

        var provisioner = svc?.PlayPlayProvisioner;
        var diag = provisioner?.GetDiagnostics();
        var status = provisioner?.GetSnapshot() ?? svc?.Playback.RuntimeStatus.Value;

        var body = new List<Element>(12) { CompiledInCard(diag, provisioner is not null) };
        if (status is { } s) body.Add(StatusSection(s));
        if (diag is { } d)
        {
            body.Add(LocateSection(d));
            body.Add(CandidatesSection(d));
            body.Add(VerifySection(d));
        }
        body.Add(ModulesSection(svc?.Modules, () => _refresh.Value = _refresh.Peek() + 1));
        body.Add(UpdatesSection(svc));
        body.Add(Actions(hooks, diag, status));
        body.Add(Caption("This report is what the provisioner already computed while resolving a runtime — reading it "
                       + "changes nothing. Attach it (or the log folder) to a bug report."));

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

    /// <summary>The headline the old generic message never said out loud: does this binary contain local-playback
    /// support at all? A "no" here means no amount of setup in the app can help — the build is the problem.</summary>
    static Element CompiledInCard(PlaybackRuntimeDiagnostics? diag, bool hasProvisioner)
    {
        if (!hasProvisioner || diag is null)
        {
            return Status(Icons.StatusInfo, Tok.SystemFillAttention, "No playback session yet",
                "Local playback is provisioned by the live Spotify session. Sign in (and let the session start) to see "
              + "the runtime search this page reports on.");
        }
        return diag.CompiledIn
            ? Status(Icons.StatusSuccess, Tok.SystemFillSuccess, "Local playback support is compiled into this build",
                "The app can search for and load a local Spotify.dll. Anything below is about THAT search.")
            : Status(Icons.StatusError, Tok.SystemFillCritical, "This build has no local-playback support",
                diag.LocateReason ?? NullPlayPlayProvisioner.NotCompiledInDetail);
    }

    static Element StatusSection(PlaybackRuntimeStatus s) => Card("Current status",
        Row("Outcome", s.Outcome.ToString()),
        Row("Detail", s.Detail),
        Row("Pack", s.PackId),
        Row("Spotify version", s.SpotifyVersion),
        Row("Architecture", s.Arch?.ToString()),
        Row("Runtime path", s.RuntimePath),
        Row("Signature trust", s.SignatureTrust.ToString()));

    static Element LocateSection(PlaybackRuntimeDiagnostics d) => Card("Locate",
        Row("Outcome", d.LocateOutcome.ToString()),
        Row("Reason", d.LocateReason));

    /// <summary>Every place that was looked at, in the order the locator walks them. An empty list is itself the
    /// answer ("nothing was probed"), so it is stated rather than rendered as a blank box.</summary>
    static Element CandidatesSection(PlaybackRuntimeDiagnostics d)
    {
        if (d.Candidates.Count == 0)
            return Card("Candidates", Body("No candidate locations were probed."));

        var rows = new List<Element>(d.Candidates.Count * 2 + 1);
        for (int i = 0; i < d.Candidates.Count; i++)
        {
            var c = d.Candidates[i];
            if (i > 0) rows.Add(new BoxEl { Height = 1f, Fill = Tok.StrokeCardDefault, Margin = new Edges4(0, 6f, 0, 6f) });
            rows.Add(new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                Children =
                [
                    new TextEl(c.Source) { Size = 12f, Weight = 600, Color = Tok.TextPrimary },
                    Chip("Spotify.dll", c.DllPresent),
                    Chip("playplay-runtime.json", c.ManifestPresent),
                ],
            });
            rows.Add(new TextEl(string.IsNullOrWhiteSpace(c.RuntimeDir) ? "(no path)" : c.RuntimeDir)
                { Size = 12f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap });
        }
        return Card("Candidates", rows.ToArray());
    }

    static Element VerifySection(PlaybackRuntimeDiagnostics d)
    {
        if (d.VerifyOutcome is null && d.VerifyDetail is null && d.SignatureTrust is null && d.SignatureInfo is null)
            return Card("Verify", Body("Verification was never reached — the search ended before a candidate could be checked."));

        var rows = new List<Element>(8)
        {
            Row("Outcome", d.VerifyOutcome?.ToString()),
            Row("Detail", d.VerifyDetail),
            Row("Signature trust", d.SignatureTrust?.ToString()),
        };
        if (d.SignatureInfo is { } sig)
        {
            rows.Add(Row("Publisher", sig.Subject));
            rows.Add(Row("Issuer", sig.Issuer));
            rows.Add(Row("Thumbprint", sig.Thumbprint));
            rows.Add(Row("Reason", sig.Reason));
            rows.Add(Row("Valid", Stamp(sig.ValidFrom) + "  →  " + Stamp(sig.ValidTo)));
            rows.Add(Row("File", sig.FilePath));
        }
        return Card("Verify", rows.ToArray());
    }

    /// <summary>The PLAYBACK MODULES section: one block per installed module (state, PID, directory, version, protocol,
    /// counters and the last error) plus every directory that was probed and REFUSED, with the reason — the same
    /// "nothing happened is never an answer" discipline the runtime locator's Candidates block follows. A module that
    /// is <c>Faulted</c> gets a Retry button; everything else is read-only.</summary>
    static Element ModulesSection(Wavee.Backend.Modules.ModuleHost? host, Action refresh)
    {
        if (host is null)
            return Card("Playback modules", Body("This build has no playback-module host (the fake backend)."));

        var rows = new List<Element>(16);
        var installed = host.Installed;
        if (installed.Count == 0)
        {
            rows.Add(Body("No playback modules are installed. Bundled modules live in "
                        + host.Catalog.BundledRoot + "; installed ones in " + host.Catalog.UserRoot + "."));
        }

        for (int i = 0; i < installed.Count; i++)
        {
            var m = installed[i];
            var process = host.ProcessFor(m.Id);
            var stats = process?.Stats.Snapshot();
            if (i > 0) rows.Add(new BoxEl { Height = 1f, Fill = Tok.StrokeCardDefault, Margin = new Edges4(0, 6f, 0, 6f) });
            rows.Add(new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                Children =
                [
                    new TextEl(m.Manifest.DisplayName) { Size = 12f, Weight = 600, Color = Tok.TextPrimary },
                    Chip(m.Bundled ? "bundled" : "installed", present: true),
                    Chip(process is null ? "Stopped" : process.State.ToString(),
                        present: process is null || process.State is Wavee.Backend.Modules.ModuleProcessState.Ready
                                                 or Wavee.Backend.Modules.ModuleProcessState.Stopped
                                                 or Wavee.Backend.Modules.ModuleProcessState.Starting),
                ],
            });
            rows.Add(Row("Id", m.Id));
            rows.Add(Row("Version", m.Version + "  ·  protocol " + m.Manifest.ProtocolVersion.ToString(CultureInfo.InvariantCulture)));
            rows.Add(Row("Publisher", m.Manifest.Publisher));
            rows.Add(Row("Directory", m.Dir));
            rows.Add(Row("Process", process?.ProcessId is { } pid
                ? "pid " + pid.ToString(CultureInfo.InvariantCulture)
                : "not running"));
            rows.Add(Row("Capabilities", string.Join(", ", m.Manifest.Capabilities ?? [])));
            if (stats is { } s)
            {
                rows.Add(Row("Requests", s.Requests.ToString(CultureInfo.InvariantCulture)
                    + "  ·  " + s.Failures.ToString(CultureInfo.InvariantCulture) + " failed"
                    + "  ·  " + s.Restarts.ToString(CultureInfo.InvariantCulture) + " restarts"));
                rows.Add(Row("Latency", "p50 " + s.P50Ms.ToString(CultureInfo.InvariantCulture)
                    + " ms  ·  p95 " + s.P95Ms.ToString(CultureInfo.InvariantCulture) + " ms"));
                if (s.LastError is { Length: > 0 })
                    rows.Add(Row("Last error", (s.LastErrorMethod ?? "") + ": " + s.LastError));
            }

            if (process?.Status is { } card) rows.Add(Row("Status", card.State + (card.Message is { Length: > 0 } msg ? " — " + msg : "")));
            if (process?.State is Wavee.Backend.Modules.ModuleProcessState.Faulted
                              or Wavee.Backend.Modules.ModuleProcessState.Crashed)
            {
                rows.Add(HStack(8f, Button.Standard("Retry", () => { host.Retry(m.Id); refresh(); })));
            }
        }

        var rejections = host.Catalog.Rejections;
        if (rejections.Count > 0)
        {
            rows.Add(new BoxEl { Height = 1f, Fill = Tok.StrokeCardDefault, Margin = new Edges4(0, 8f, 0, 6f) });
            rows.Add(new TextEl("Refused") { Size = 12f, Weight = 600, Color = Tok.TextSecondary });
            for (int i = 0; i < rejections.Count; i++)
            {
                rows.Add(new TextEl(rejections[i].Dir) { Size = 12f, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap });
                rows.Add(new TextEl(rejections[i].Reason) { Size = 12f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap });
            }
        }

        return Card("Playback modules", rows.ToArray());
    }

    /// <summary>The update pillar's own receipts. It lives on THIS page (rather than a second diagnostics surface)
    /// because the questions are the same shape — "which feed, which build, what did the OS actually do" — and a user
    /// who is told to send a report should have to copy one thing.
    ///
    /// <para>"Repair auto-update" hands the feed URL to the browser, which DOWNLOADS the <c>.appinstaller</c> file;
    /// the user then has to open it, and Windows App Installer takes over from there. That two-step is what the hint
    /// under the link says, because the link on its own looks like it did nothing — a small file lands in Downloads
    /// and no UI appears. It is a real repair rather than a diagnostic: installing from the feed is what writes the
    /// association that <c>AutoUpdateAssociated</c> reports, and it is the only supported way to (re-)establish it
    /// from outside a package deployment.</para></summary>
    static Element UpdatesSection(Services? svc)
    {
        var me = AppVersion.Info;
        var upd = svc?.AppUpdate;
        var snap = upd?.Current ?? Wavee.Core.AppUpdateSnapshot.Idle;
        string feed = upd?.FeedUrl is { Length: > 0 } f ? f : "—";

        var rows = new List<Element>(9)
        {
            Row(Loc.Get(Strings.Diagnostics.Updates.Feed), feed),
            Row(Loc.Get(Strings.Diagnostics.Updates.Channel), me.Channel),
            Row(Loc.Get(Strings.Diagnostics.Updates.Version), me.Quad is { Length: > 0 } q ? q : me.SemVer),
            Row(Loc.Get(Strings.Diagnostics.Updates.State), snap.State.ToString()),
            Row(Loc.Get(Strings.Diagnostics.Updates.Associated), Loc.Get(snap.AutoUpdateAssociated
                ? Strings.Diagnostics.Updates.AssociatedYes
                : Strings.Diagnostics.Updates.AssociatedNo)),
            Row(Loc.Get(Strings.Diagnostics.Updates.LastChecked), snap.LastCheckedMs > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(snap.LastCheckedMs).ToLocalTime()
                    .ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)
                : null),
            Row(Loc.Get(Strings.Diagnostics.Updates.LastFailure), snap.Failure is { } fail
                ? fail.Kind + "  ·  0x" + fail.HResult.ToString("X8", CultureInfo.InvariantCulture)
                  + (fail.Message is { Length: > 0 } fm ? "  ·  " + fm : "")
                : null),
        };

        // The notes pipeline's own receipts: which rung of embedded -> cache -> release asset answered, what the last
        // network attempt did, and how much of the unauthenticated GitHub budget this process has spent.
        if (svc?.ReleaseNotes is { } notes)
        {
            var nd = notes.DiagnosticsSnapshot();
            rows.Add(Row(Loc.Get(Strings.Diagnostics.Updates.NotesSource), nd.LastSource));
            rows.Add(Row(Loc.Get(Strings.Diagnostics.Updates.NotesCache), nd.CacheRoot));
            rows.Add(Row(Loc.Get(Strings.Diagnostics.Updates.NotesEmbedded), nd.EmbeddedRoot));
            rows.Add(Row(Loc.Get(Strings.Diagnostics.Updates.NotesFeed), nd.FeedRelease));
            rows.Add(Row(Loc.Get(Strings.Diagnostics.Updates.NotesLastFetch), nd.LastFetchUtc is { } at
                ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)
                  + (nd.LastFetchUrl.Length > 0 ? "  ·  " + nd.LastFetchUrl : "")
                  + (nd.LastFetchStatus.Length > 0 ? "  ·  " + nd.LastFetchStatus : "")
                : null));
            rows.Add(Row(Loc.Get(Strings.Diagnostics.Updates.NotesBudget),
                Strings.Diagnostics.Updates.NotesBudgetValue(nd.IssueRequestsThisSession, nd.RateLimitRemaining)));
        }

        if (upd is not null)
            rows.Add(new BoxEl
            {
                Direction = 1, Gap = 2f, Margin = new Edges4(0f, 6f, 0f, 0f),
                Children =
                [
                    HyperlinkButton.Create(Loc.Get(Strings.Diagnostics.Updates.Repair), () => LoginView.OpenUrl(feed)),
                    Caption(Loc.Get(Strings.Diagnostics.Updates.RepairHint)),
                ],
            });

        return Card(Loc.Get(Strings.Diagnostics.Updates.Title), rows.ToArray());
    }

    Element Actions(InputHooks hooks, PlaybackRuntimeDiagnostics? diag, PlaybackRuntimeStatus? status) => HStack(8f,
        Button.Accent("Copy diagnostics", () =>
        {
            hooks.Clipboard?.SetText(BuildReport(diag, status));
            Toast.Show("Diagnostics copied", new ToastOptions { Severity = InfoBarSeverity.Success });
        }),
        Button.Standard("Open log folder", () =>
            SettingsShared.OpenFolder(Path.GetDirectoryName(WaveeLog.Instance.FilePath ?? "") ?? SettingsShared.AppDataRoot)),
        Button.Standard("Refresh", () => _refresh.Value = _refresh.Peek() + 1));

    // ── The structured text dump (clipboard + bug reports) ────────────────────────────────────────

    internal static string BuildReport(PlaybackRuntimeDiagnostics? diag, PlaybackRuntimeStatus? status)
    {
        var sb = new StringBuilder(1024);
        sb.Append("Wavee — playback runtime diagnostics\n");
        sb.Append("captured : ").Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("log file : ").Append(WaveeLog.Instance.FilePath ?? "(none)").Append("\n\n");

        if (status is { } s)
        {
            sb.Append("[status]\n");
            Line(sb, "outcome", s.Outcome.ToString());
            Line(sb, "detail", s.Detail);
            Line(sb, "packId", s.PackId);
            Line(sb, "spotifyVersion", s.SpotifyVersion);
            Line(sb, "arch", s.Arch?.ToString());
            Line(sb, "runtimePath", s.RuntimePath);
            Line(sb, "signatureTrust", s.SignatureTrust.ToString());
            Line(sb, "trustedByPinnedFingerprint", s.TrustedByPinnedFingerprint ? "yes" : "no");
            sb.Append('\n');
        }

        if (diag is null)
        {
            sb.Append("[diagnostics]\n  unavailable — no playback session is running.\n");
            return sb.ToString();
        }

        sb.Append("[build]\n");
        Line(sb, "localPlaybackCompiledIn", diag.CompiledIn ? "yes" : "no");
        sb.Append("\n[locate]\n");
        Line(sb, "outcome", diag.LocateOutcome.ToString());
        Line(sb, "reason", diag.LocateReason);

        sb.Append("\n[candidates] (").Append(diag.Candidates.Count.ToString(CultureInfo.InvariantCulture)).Append(")\n");
        foreach (var c in diag.Candidates)
        {
            sb.Append("  - ").Append(c.Source).Append('\n');
            sb.Append("      dir      : ").Append(string.IsNullOrWhiteSpace(c.RuntimeDir) ? "(none)" : c.RuntimeDir).Append('\n');
            sb.Append("      dll      : ").Append(c.DllPresent ? "present" : "missing").Append('\n');
            sb.Append("      manifest : ").Append(c.ManifestPresent ? "present" : "missing").Append('\n');
        }

        sb.Append("\n[verify]\n");
        Line(sb, "outcome", diag.VerifyOutcome?.ToString());
        Line(sb, "detail", diag.VerifyDetail);
        Line(sb, "signatureTrust", diag.SignatureTrust?.ToString());
        if (diag.SignatureInfo is { } sig)
        {
            Line(sb, "publisher", sig.Subject);
            Line(sb, "issuer", sig.Issuer);
            Line(sb, "thumbprint", sig.Thumbprint);
            Line(sb, "reason", sig.Reason);
            Line(sb, "validFrom", Stamp(sig.ValidFrom));
            Line(sb, "validTo", Stamp(sig.ValidTo));
            Line(sb, "file", sig.FilePath);
        }
        return sb.ToString();
    }

    static void Line(StringBuilder sb, string label, string? value) =>
        sb.Append("  ").Append(label).Append(" : ").Append(string.IsNullOrWhiteSpace(value) ? "(none)" : value).Append('\n');

    static string Stamp(DateTimeOffset t) =>
        t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

    // ── Chrome ────────────────────────────────────────────────────────────────────────────────────

    static Element PageHeader() => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
        Padding = new Edges4(Spacing.L, Spacing.L, Spacing.L, Spacing.M),
        Children =
        [
            Icon(Icons.MusicNote, 22f, Tok.TextPrimary),
            WaveeType.PageHero(Loc.Get(Strings.Playback.Runtime.DiagnosticsTitle)) with { Grow = 1f },
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
            new TextEl(label) { Size = 12f, Color = Tok.TextSecondary, Width = 132f, Shrink = 0f },
            new TextEl(string.IsNullOrWhiteSpace(value) ? "—" : value)
                { Size = 12f, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, Grow = 1f },
        ],
    };

    static Element Chip(string label, bool present) => new BoxEl
    {
        Padding = new Edges4(6f, 1f, 6f, 1f), Corners = CornerRadius4.All(Radii.Control),
        Fill = present ? Tok.SystemFillSuccessBackground : Tok.SystemFillCriticalBackground,
        Children = [ new TextEl((present ? "✓ " : "✕ ") + label) { Size = 11f, Color = Tok.TextPrimary } ],
    };

    static Element Body(string text) => new TextEl(text) { Size = 12f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap };

    static Element Caption(string text) => new TextEl(text) { Size = 11f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap };

    /// <summary>A coloured status glyph + heading, then wrapped body copy — the same inline block the setup dialog
    /// uses, so the two surfaces read as one feature.</summary>
    static Element Status(string glyph, ColorF glyphColor, string heading, string body) => new BoxEl
    {
        Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Start,
        Padding = Edges4.All(12f), Fill = Tok.FillLayerAlt, Corners = CornerRadius4.All(Radii.Control),
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        Children =
        [
            new TextEl(glyph) { Size = 18f, FontFamily = Theme.IconFont, Color = glyphColor, Shrink = 0f, Margin = new Edges4(0, 1f, 0, 0) },
            new BoxEl
            {
                Direction = 1, Gap = 4f, Grow = 1f,
                Children =
                [
                    new TextEl(heading) { Size = 14f, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap },
                    new TextEl(body) { Size = 13f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap },
                ],
            },
        ],
    };
}
