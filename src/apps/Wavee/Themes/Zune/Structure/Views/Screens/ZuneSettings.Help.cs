// ── Themes/Zune/Structure/Views/Screens/ZuneSettings.Help.cs — Help & about's sections (themes plan 6.3, Z8b; settings rework phase 4) ────────────────
//
// Boards Z-Settings-Sections ("about": the version as a numeral, the update line, the doors; "keyboard": the chords as small outlined caps) and Z-Feedback (the
// three ways to tell the developer as disc rows, what a report holds). The update hero (the version as a numeral, the build line, the primary action as the round
// accent button with up to two commands) with the status row, the receipts ("wavee right now", a 5 s interval on its child), the licences, the log viewer (its
// words count the live session), the two detail levels, the log files and realtime capture with its two doors.
//
// Everything with LIVE state is an embedded child component: the update panel reads `Notify.Update`, the receipts own their interval, the log viewer its tick,
// the detail levels the settings-changed signal. The hero's state matrix (`Settings.AboutRules`), the verbs (the dials' writers) and the notices file are the core's.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using AboutRules = Wavee.Settings.AboutRules;
using PrivacyRules = Wavee.Settings.PrivacyRules;
using PvLogs = Wavee.Strings.Settings.Privacy.Logs;
using Z = Wavee.Themes.Zune.ZuneSettingsRules;

namespace Wavee.Themes.Zune;

internal static partial class ZuneSettings
{
    // ══ the update panel ═════════════════════════════════════════════════════════════════════════════════════════════════

    sealed record UpdateArgs(SettingsBodyRow Row);

    /// <summary>The hero and the status row, keyed as the hero dial's row. Re-renders on every published snapshot (it reads `Notify.Update`). The hero's verb and the
    /// status row's two doors (open the failed release's page, dismiss after an update) are the dial and its children.</summary>
    sealed class UpdatePanel : ZuneScreenComponent
    {
        public override Element Render()
        {
            var k = new ZuneKit(UseLayout());
            var row = UseProps<UpdateArgs>().Row;
            var s = Notify.Update.Value;
            var me = Platform.Version;
            var kids = new List<Element>(2) { Hero(k, me, s, row) };
            if (!me.IsStore) kids.Add(StatusRow(k, s, row));
            return new BoxEl
            {
                Key = SettingsKeys.ZuneRow(row.Id), Direction = 1, Gap = Z.SectionGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, OnRealized = NoteRow(row.Id),
                Children = kids.ToArray(),
            };
        }

        /// <summary>The version as a numeral, the line under it (the build quad in mono, the channel, the state, the commit and arch, the provenance) and the primary action as the
        /// round accent button with its word beside it, with up to two commands.</summary>
        static Element Hero(ZuneKit k, WaveeVersionInfo me, AppUpdateSnapshot s, SettingsBodyRow row)
        {
            // A dev build has nothing for the feed to be newer than, unless a developer-mode SIMULATION is walking, whose states are real enough to press.
            var shape = AboutRules.Hero(s.State, me.IsDev && !Update.Host.IsSimulating, me.IsStore);
            string channel = Loc.Get(me.Channel switch
            {
                "beta" => Strings.Update.About.ChannelBeta,
                "stable" => Strings.Update.About.ChannelStable,
                _ => Strings.Update.About.ChannelDev,
            });
            var line = new List<string>(4);
            if (me.Quad.Length > 0) line.Add(me.Quad);
            line.Add(channel);
            line.Add(Loc.Get(shape.PillKey));
            line.Add((me.Commit.Length > 0 ? me.Commit + SummarySeparator : "") + Settings.ArchToken);

            var hero = row.Writer;
            Action act = shape.Verb == AboutRules.HeroVerb.None ? static () => { } : () => hero.Run?.Invoke();
            long lastChecked = s.LastCheckedMs > 0 ? s.LastCheckedMs : SettingsFacts.UpdateLastCheckedMs();
            string provenance = AboutRules.Provenance(me.BuildDate, lastChecked, static d => Strings.Update.About.Built(d),
                static w => Strings.Update.About.LastChecked(w), Loc.Get(Strings.Update.About.NeverChecked));

            var doors = new List<Element>(3)
            {
                k.Go(Loc.Get(shape.ButtonKey), act, shape.Tone != AboutRules.HeroTone.Disabled,
                    shape.Tone == AboutRules.HeroTone.Accent ? Icons.Forward : Icons.Refresh),
            };
            if (me.Codename.Length > 0 && SettingsSpec.TryDial(WhatsNewDial, out var whatsNew))
                doors.Add(k.Cmd(Strings.Update.About.WhatsNewIn(me.Codename), () => whatsNew.Writer.Run?.Invoke()));

            return new BoxEl
            {
                Key = "zs:about:hero", Direction = 1, Gap = Z.LabelGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
                Children =
                [
                    ZuneText.Cased(R.Type.Hero(ReleaseNotes.VersionDisplayText()), LabelKind.Heading) with { MinWidth = 0f, Wrap = TextWrap.Wrap },
                    R.Type.DenseMeta(string.Join("  ·  ", line)) with { Color = Tok.TextSecondary, MinWidth = 0f, Wrap = TextWrap.Wrap, FontFamily = Z.Mono },
                    ZuneText.Hint(provenance),
                    new BoxEl
                    {
                        Direction = 0, Wrap = true, AlignItems = FlexAlign.Center, Gap = Z.LinkGap, MinWidth = 0f, Margin = new Edges4(0f, Z.ControlGap, 0f, 0f),
                        Children = doors.ToArray(),
                    },
                ],
            };
        }

        /// <summary>The status row: the ONE state sentence (`Notify.AppUpdateToasts.StateSentence`), plus progress while downloading or installing, "open release page" on a
        /// failure, "dismiss" after an update (the hero dial's children).</summary>
        static Element StatusRow(ZuneKit k, AppUpdateSnapshot s, SettingsBodyRow row)
        {
            SettingsBodyRow? Child(string id)
            {
                foreach (var child in row.Children)
                    if (string.Equals(child.Id, id, StringComparison.Ordinal)) return child;
                return null;
            }
            Element? Door(string id, string word) => Child(id) is { } child
                ? k.Cmd(word, RunOf(child)) with { Key = SettingsKeys.ZuneRow(child.Id), OnRealized = NoteRow(child.Id) }
                : null;
            Element? content = s.State switch
            {
                AppUpdateState.Downloading or AppUpdateState.Installing => new BoxEl
                {
                    Direction = 0, Gap = Z.ControlGap, AlignItems = FlexAlign.Center, Shrink = 0f,
                    Children =
                    [
                        ZuneKit.Bar(s.ProgressPercent / 100f, Z.UpdateProgressWidth),
                        R.Type.DenseMeta(s.ProgressPercent.ToString(CultureInfo.InvariantCulture) + "%") with { Color = Tok.TextSecondary, FontFamily = Z.Mono },
                    ],
                },
                // The release that FAILED, not the bare listing (`Update.ReleasePageUrl` is the one owner of that rule).
                AppUpdateState.Failed => Door(OpenReleasePageDial, Loc.Get(Strings.Update.Action.OpenReleasePage)),
                AppUpdateState.Completed => Door(DismissUpdateDial, Loc.Get(Strings.Update.Action.Dismiss)),
                _ => null,
            };
            return k.Row("zs:row:update-status", Loc.Get(Strings.Update.Status.Title), Notify.AppUpdateToasts.StateSentence(s), content);
        }
    }

    const string OpenReleasePageDial = "help.update.openReleasePage", DismissUpdateDial = "help.update.dismiss";

    // ══ "WAVEE RIGHT NOW" ════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The receipts under their label, keyed as the receipts dial's row.</summary>
    static Element ReceiptsBlock(SettingsBodyRow row) => new BoxEl
    {
        Key = SettingsKeys.ZuneRow(row.Id), Direction = 1, Gap = Z.RowStackGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, OnRealized = NoteRow(row.Id),
        Children = [ZuneText.Label(row.Label), Embed.Comp(static () => new ReceiptsRows()) with { Key = "zs:receipts" }],
    };

    /// <summary>A 5 000 ms interval on THIS child; Render never reads process, GPU or FPS.</summary>
    sealed class ReceiptsRows : ZuneScreenComponent
    {
        readonly Signal<string> _gpu = new(Settings.Receipts.Dash), _workingSet = new(Settings.Receipts.Dash), _managed = new(Settings.Receipts.Dash),
            _uptime = new(Settings.Receipts.Dash), _fps = new(Settings.Receipts.Dash), _zoom = new(Settings.Receipts.Dash), _gpuAssets = new(Settings.Receipts.Dash),
            _appExcl = new(Settings.Receipts.Dash), _detail = new(Settings.Receipts.Dash);

        public override Element Render()
        {
            var k = new ZuneKit(UseLayout());
            UseEffect(Tick, DepKey.Empty);
            UseInterval(Tick, Z.ReceiptsTickMs);
            return new BoxEl
            {
                Key = "zs:receipts:rows", Direction = 1, Gap = Z.LabelGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
                Children =
                [
                    Line(k, "GPU", _gpu), Line(k, "Working set", _workingSet), Line(k, "Managed heap", _managed), Line(k, "Uptime", _uptime),
                    Line(k, "FPS", _fps), Line(k, "Zoom", _zoom), Line(k, "GPU assets", _gpuAssets), Line(k, "App memory excl. GPU assets", _appExcl),
                    ZuneText.Hint(_detail.Value),
                ],
            };
        }

        static Element Line(ZuneKit k, string label, Signal<string> value) => k.Row("zs:receipt:" + label, label, null,
            R.Type.DenseMeta(value.Value) with { Color = Tok.TextPrimary, MinWidth = 0f, Wrap = TextWrap.Wrap, Grow = 1f });

        void Tick()
        {
            var sample = Settings.SampleReceipts();
            _gpu.Value = sample.Gpu;
            _workingSet.Value = sample.WorkingSet;
            _managed.Value = sample.Managed;
            _uptime.Value = sample.Uptime;
            _fps.Value = sample.Fps;
            _zoom.Value = sample.Zoom;
            _gpuAssets.Value = sample.GpuAssets;
            _appExcl.Value = sample.AppExclusive;
            _detail.Value = sample.Detail;
        }
    }

    // ══ licences ═════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The two licences under their label, keyed as the licences dial's row.</summary>
    static Element LicensesBlock(SettingsBodyRow row) => new BoxEl
    {
        Key = SettingsKeys.ZuneRow(row.Id), Direction = 1, Gap = Z.RowStackGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, OnRealized = NoteRow(row.Id),
        Children =
        [
            ZuneText.Label(row.Label),
            Embed.Comp(static () => new LicenseRow("Wavee", "MIT", Settings.WaveeLicense)) with { Key = "zs:license:wavee" },
            Embed.Comp(static () => new LicenseRow(Loc.Get(Strings.Settings.About.ThirdPartyNotices), Settings.NoticesFileName, Settings.ReadNotices())) with { Key = "zs:license:third-party" },
        ],
    };

    /// <summary>A licence: its name and kind, and a command that opens and closes the text (12-DIP mono, tertiary). Closed until asked for.</summary>
    sealed class LicenseRow(string name, string kind, string body) : ZuneScreenComponent
    {
        readonly Signal<bool> _open = new(false);

        public override Element Render()
        {
            var k = new ZuneKit(UseLayout());
            bool open = _open.Value;
            var kids = new List<Element>(2)
            {
                k.Row("zs:row:license:" + name, name, kind, k.Cmd(Loc.Get(open ? Strings.Common.Less : Strings.Common.More), () => _open.Value = !_open.Peek())),
            };
            if (open)
                kids.Add(R.Type.MicroMeta(body) with { Key = "zs:license:body:" + name, Color = Tok.TextTertiary, FontFamily = Z.Mono, Wrap = TextWrap.Wrap, MinWidth = 0f });
            return new BoxEl { Direction = 1, Gap = Z.LabelGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Children = kids.ToArray() };
        }
    }

    // ══ feedback ═════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The marks on the three discs: report a problem, suggest something, ask a question.</summary>
    const string ActionMarkReport = "!", ActionMarkSuggest = "+", ActionMarkAsk = "?";

    /// <summary>One of the three ways to tell the developer (Z-Feedback): a disc with a mark, the words, a chevron. A press opens the report dialog (the dial's writer).</summary>
    static Element ActionRow(SettingsBodyRow row, string mark, bool accent)
    {
        var kids = new List<Element>(2) { ZuneText.Label(row.Label) };
        if (row.Description is { Length: > 0 } sub) kids.Add(ZuneText.Hint(sub));
        Element disc = new BoxEl
        {
            Width = Z.DiscSize, Height = Z.DiscSize, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
            Corners = CornerRadius4.All(ZuneControls.Round), Fill = accent ? Tok.AccentDefault : default,
            BorderWidth = accent ? 0f : Z.KeyChipStroke * 2f, BorderColor = Tok.TextPrimary,
            Children = [R.Type.TrackTitle(mark) with { Color = accent ? Tok.TextOnAccentPrimary : Tok.TextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap }],
        };
        return new BoxEl
        {
            Key = SettingsKeys.ZuneRow(row.Id), Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.ActionGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, MinHeight = Z.ActionMinH,
            Padding = new Edges4(Z.ActionPadX, Z.ActionPadY, Z.ActionPadX, Z.ActionPadY), OnRealized = NoteRow(row.Id),
            // The row's hover plate bleeds past the column by its padding, so the disc stands on the column's edge (Z-Feedback).
            Margin = new Edges4(-Z.ActionPadX, 0f, -Z.ActionPadX, 0f),
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = RunOf(row),
            Children =
            [
                disc,
                new BoxEl { Direction = 1, Gap = Z.LabelGap, Grow = 1f, Basis = 0f, MinWidth = 0f, Children = kids.ToArray() },
                new BoxEl { Shrink = 0f, Children = [Icon(Icons.ChevronRight, Z.SectionChevron, Tok.TextTertiary)] },
            ],
        }.Interactive(Interaction.Subtle);
    }

    /// <summary>What a report holds: the four lines the report dialog states (what the reporter writes, the versions, the last log lines if ticked, never secrets), then the
    /// dialog's own sentence on what is sent and when. Keyed as the dial's row.</summary>
    static Element HoldsCard(ZuneKit k, SettingsBodyRow row)
    {
        Element Line(string key, bool yes, string text) => new BoxEl
        {
            Key = key, Direction = 0, Gap = Z.HoldsGap, AlignItems = FlexAlign.Start, MinWidth = 0f,
            Children =
            [
                new BoxEl { Shrink = 0f, Children = [Icon(yes ? Icons.Accept : Icons.Cancel, Z.HoldsMark, yes ? Tok.SystemFillSuccess : Tok.SystemFillCritical)] },
                ZuneText.Label(text) with { Grow = 1f, Basis = 0f },
            ],
        };
        return new BoxEl
        {
            Key = SettingsKeys.ZuneRow(row.Id), Direction = 1, Gap = Z.RowGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, OnRealized = NoteRow(row.Id),
            Children =
            [
                ZuneText.Label(row.Label) with { Color = Tok.TextSecondary },
                Line("zs:holds:write", true, Loc.Get(Strings.Settings.Feedback.HoldsWrite)),
                Line("zs:holds:versions", true, Loc.Get(Strings.Settings.Feedback.HoldsVersions)),
                Line("zs:holds:logs", true, Strings.Settings.Feedback.HoldsLogs(Feedback.ReportBundle.ManualLogLines)),
                Line("zs:holds:never", false, Loc.Get(Strings.Settings.Feedback.HoldsNever)),
                ZuneText.Hint(Loc.Get(Strings.Report.Subtitle)),
            ],
        };
    }

    // ══ keyboard ═════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The cap stands at the right end of its row (Z-Settings-Sections), in a box that fills the control column.</summary>
    static Element KeyChip(string chord) => new BoxEl
    {
        Direction = 0, Grow = 1f, Justify = FlexJustify.End, MinWidth = 0f, Children = [KeyCap(chord)],
    };

    static Element KeyCap(string chord) => new BoxEl
    {
        Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Padding = new Edges4(Z.KeyChipPadX, Z.KeyChipPadY, Z.KeyChipPadX, Z.KeyChipPadY),
        BorderWidth = Z.KeyChipStroke, BorderColor = Tok.StrokeCardDefault, HitTestVisible = false,
        Children = [ZuneText.Cased(R.Type.MicroMeta(Settings.Shortcuts.Words(chord)), LabelKind.Label) with { Color = Tok.TextSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap }],
    };

    // ══ diagnostics ══════════════════════════════════════════════════════════════════════════════════════════════════════

    sealed record LogArgs(SettingsBodyRow Row);

    /// <summary>The log viewer row: opens the viewer page, and its words count the live session. A 750 ms interval on THIS row writes only when the log or the minute
    /// moved, so one line of text re-renders and nothing runs per frame.</summary>
    sealed class LogViewerRow : ZuneScreenComponent
    {
        readonly Signal<string> _line = new(Compose());
        long _seenVersion = -1, _seenMinute = -1;

        public override Element Render()
        {
            var k = new ZuneKit(UseLayout());
            var row = UseProps<LogArgs>().Row;
            UseInterval(() =>
            {
                long version = Log.Version, minute = Log.SinceStartMs / Z.MillisPerMinute;
                if (version == _seenVersion && minute == _seenMinute) return;
                _seenVersion = version;
                _seenMinute = minute;
                _line.Value = Compose();
            }, Z.LogTickMs);
            return k.Row(SettingsKeys.ZuneRow(row.Id), row.Label, _line.Value, k.Cmd(row.Label, RunOf(row))) with { OnRealized = NoteRow(row.Id) };
        }

        static string Compose()
        {
            Log.Tally(out int total, out int warnings, out int errors);
            return PvLogs.ViewerSub(Thousands(total), Thousands(warnings), Thousands(errors), Diagnostics.LogView.Uptime(TimeSpan.FromMilliseconds(Log.SinceStartMs)));
        }

        static string Thousands(int n) => n.ToString("N0", CultureInfo.InvariantCulture);
    }

    /// <summary>The two detail levels (the dial's children): what the viewer captures and what reaches the log file. Both are persisted settings, so the group subscribes
    /// <see cref="Platform.SettingsChanged"/> (the viewer's Verbose toggle writes the same keys). A combo's items and selection freeze at mount, so each is keyed by the
    /// level VALUE it shows.</summary>
    sealed class DetailLevelRows : ZuneScreenComponent
    {
        public override Element Render()
        {
            var k = new ZuneKit(UseLayout());
            var row = UseProps<LogArgs>().Row;
            _ = Platform.SettingsChanged.Value;   // subscribed
            int last = Diagnostics.LogView.LevelNames.Length - 1;
            var kids = new List<Element>(row.Children.Length);
            foreach (var child in row.Children)
            {
                int index = Math.Clamp(child.Dial.Current is int at ? at : 0, 0, last);
                var writer = child.Writer;
                kids.Add(k.Row(SettingsKeys.ZuneRow(child.Id), child.Label, child.Description,
                    k.Choice(child.Id + ":" + index.ToString(CultureInfo.InvariantCulture), new ZuneChoice(Diagnostics.LogView.LevelNames, () => index,
                        i => writer.Choose?.Invoke(i), Chips: false, Width: Z.LevelComboWidth))) with { OnRealized = NoteRow(child.Id) });
            }
            return new BoxEl
            {
                Key = SettingsKeys.ZuneRow(row.Id), Direction = 1, Gap = Z.RowGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, OnRealized = NoteRow(row.Id),
                Children = kids.ToArray(),
            };
        }
    }

    /// <summary>The log files: the size, retention and age rule as the description, "open folder" (the dial's writer).</summary>
    static Element LogFilesRow(Paint d, SettingsBodyRow row)
        => DialRow(d, row, d.K.Cmd(Loc.Get(PvLogs.OpenFolder), RunOf(row)),
            PvLogs.FilesSub(PrivacyRules.WholeMb(Log.MaxFileBytes), Log.RetentionDays.ToString(CultureInfo.InvariantCulture), PrivacyRules.WholeMb(Log.RetentionBytes)));

    /// <summary>Realtime capture: the switch (it applies live through the capture host) with its two doors beside it (its children, keyed as their rows).</summary>
    static Element CaptureRow(Paint d, SettingsBodyRow row)
    {
        var k = d.K;
        var writer = row.Writer;
        var controls = new List<Element>(3) { k.Switch(row.Value is true, on => { if (writer.Toggle?.Invoke(on) == true) Settings.Bump(); }, row.Enabled) };
        foreach (var child in row.Children)
            if (child.Kind == SettingsRowKind.Action)
                controls.Add(k.Cmd(child.Label, RunOf(child), child.Enabled) with { Key = SettingsKeys.ZuneRow(child.Id), OnRealized = NoteRow(child.Id) });
        return DialRow(d, row, new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.ControlGap, Wrap = true, MinWidth = 0f, Children = controls.ToArray() },
            row.Description);
    }
}
