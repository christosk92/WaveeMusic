using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.WindowsApi.Dialogs;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>API console page — Postman-style spclient / extended-metadata probing (normal nav route <c>api-console</c>).</summary>
sealed class ApiConsolePage : Component
{
    const float ContentMaxW = 1000f;
    const float FieldW = 920f;

    readonly Signal<int> _channel = new(0);
    readonly Signal<int> _method = new(1);
    readonly Signal<int> _bodyMode = new(2);
    readonly Signal<int> _responseTab = new(0);
    readonly Signal<int> _decode = new(0);
    readonly Signal<int> _gzipText = new(0);
    readonly Signal<int> _bulkHydration = new(0);
    readonly Signal<int> _busy = new(0);
    readonly Signal<int> _ui = new(0);

    readonly Signal<string> _url = new("/extended-metadata/v0/extended-metadata");
    readonly Signal<string> _headers = new(ApiDebugBodyBuilder.DefaultExtendedMetadataHeaders());
    readonly Signal<string> _body = new(ApiDebugBodyBuilder.DefaultEntityLinesExample());
    readonly Signal<string> _status = new("");
    readonly Signal<string> _responseHeaders = new("");
    readonly Signal<string> _responseBody = new("");
    readonly Signal<string> _requestPreview = new("");

    byte[]? _lastBody;
    string? _lastContentType;

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var hooks = UseContext(InputHooks.Current);
        var post = UsePost();
        _ = _ui.Value;
        _ = _busy.Value;
        _ = _bodyMode.Value;
        _ = _responseTab.Value;
        int bodyMode = _bodyMode.Value;
        string baseHint = svc?.RealSpclientBaseUrl?.Value is { Length: > 0 } b ? b : "(not connected)";

        void Bump() => _ui.Value = _ui.Peek() + 1;

        void OnBodyMode(int i)
        {
            if (i == 2 && _headers.Peek().Trim().Length == 0)
                _headers.Value = ApiDebugBodyBuilder.DefaultExtendedMetadataHeaders();
            if (i == 2 && _body.Peek().Trim().Length == 0)
                _body.Value = ApiDebugBodyBuilder.DefaultEntityLinesExample();
            Bump();
        }

        void PreviewRequest()
        {
            if (svc?.RealSessionHost?.Current is null)
            {
                _requestPreview.Value = "Log in to preview request bodies.";
                Bump();
                return;
            }
            if (bodyMode == 2)
            {
                if (_bulkHydration.Value != 0)
                {
                    var (gz, err) = ApiDebugBodyBuilder.BuildBulkHydration(_body.Value, svc.RealSessionHost.Current);
                    _requestPreview.Value = err ?? $"bulk hydration → {gz!.Length:N0} bytes gzip";
                }
                else
                {
                    var (lines, err) = ApiDebugBodyBuilder.ParseEntityLines(_body.Value);
                    if (err is not null) _requestPreview.Value = err;
                    else
                    {
                        var (gz, plain, _) = ApiDebugBodyBuilder.BuildExtendedMetadata(lines, svc.RealSessionHost.Current);
                        _requestPreview.Value = ApiDebugProto.ForDisplay($"{gz.Length:N0} bytes gzip\n\n{Google.Protobuf.JsonFormatter.ToDiagnosticString(plain)}");
                    }
                }
            }
            else if (bodyMode == 1)
            {
                var bytes = ApiDebugBodyBuilder.BuildTextBody(_body.Value, _gzipText.Value != 0, out var err);
                _requestPreview.Value = err ?? (bytes is { Length: > 0 }
                    ? $"{bytes.Length:N0} bytes{(_gzipText.Value != 0 ? " gzip" : " utf-8")}"
                    : "(empty)");
            }
            else _requestPreview.Value = "(no body)";
            Bump();
        }

        void SaveResponseJson()
        {
            if (_lastBody is not { Length: > 0 })
            {
                _status.Value = "No response to save.";
                Bump();
                return;
            }
            string text = ApiDebugProto.ExportJson(_lastBody, _decode.Peek(), _lastContentType);
            if (!ApiDebugProto.LooksExportable(text))
            {
                _status.Value = "Response is not exportable JSON — try a protobuf decode mode or Save raw.";
                Bump();
                return;
            }
            string? path = FilePicker.SaveFile(FluentApp.WindowHandle, "Save response JSON",
                ApiDebugProto.SuggestExportFileName(_decode.Peek(), _url.Peek()),
                ("JSON", "*.json"), ("All files", "*.*"));
            if (path is null) return;
            try
            {
                File.WriteAllText(path, text);
                _status.Value = $"Saved JSON ({text.Length:N0} chars) → {path}";
                svc?.Log.Info("probe", $"API response JSON saved → {path}");
            }
            catch (Exception ex) { _status.Value = "Save failed: " + ex.Message; }
            Bump();
        }

        void SaveResponseRaw()
        {
            if (_lastBody is not { Length: > 0 })
            {
                _status.Value = "No response to save.";
                Bump();
                return;
            }
            byte[] body = ApiDebugProto.Decompress(_lastBody);
            string? path = FilePicker.SaveFile(FluentApp.WindowHandle, "Save raw response",
                ApiDebugProto.SuggestRawFileName(_url.Peek()),
                ("Protobuf / binary", "*.bin"), ("All files", "*.*"));
            if (path is null) return;
            try
            {
                File.WriteAllBytes(path, body);
                _status.Value = $"Saved raw ({body.Length:N0} bytes) → {path}";
                svc?.Log.Info("probe", $"API response raw saved → {path}");
            }
            catch (Exception ex) { _status.Value = "Save failed: " + ex.Message; }
            Bump();
        }

        async void Send()
        {
            if (svc is null) { _status.Value = "Services unavailable."; Bump(); return; }
            if (_busy.Peek() != 0) return;
            _busy.Value = 1;
            _status.Value = "Sending…";
            Bump();
            int methodIdx = Math.Clamp(_method.Peek(), 0, ApiDebugExecutor.MethodLabels.Length - 1);
            try
            {
                var result = await ApiDebugExecutor.SendAsync(svc, _channel.Peek(), ApiDebugExecutor.MethodLabels[methodIdx],
                    _url.Value, _headers.Value, bodyMode, _body.Value, _gzipText.Value != 0, _bulkHydration.Value != 0,
                    CancellationToken.None).ConfigureAwait(false);
                post(() =>
                {
                    _lastBody = result.Body;
                    _lastContentType = result.ResponseContentType;
                    if (result.RequestPreview is { Length: > 0 } prev) _requestPreview.Value = prev;
                    if (result.Error is { Length: > 0 } err)
                        _status.Value = $"Error · {result.Elapsed} · {err}";
                    else
                        _status.Value = $"{(result.Ok ? "OK" : "HTTP")} {result.Status} · {result.Elapsed} · {result.Body.Length:N0} bytes";
                    _responseHeaders.Value = ApiDebugBodyBuilder.FormatHeaders(result.ResponseHeaders);
                    _responseBody.Value = result.Error is { Length: > 0 } && result.Body.Length == 0
                        ? result.Error
                        : ApiDebugProto.Format(result.Body, _decode.Peek(), result.ResponseContentType);
                    _busy.Value = 0;
                    svc.Log.Info("probe", $"API {_url.Peek()} → {result.Status} ({result.Body.Length} B)");
                    Bump();
                });
            }
            catch (Exception ex)
            {
                post(() => { _status.Value = "Failed: " + ex.Message; _busy.Value = 0; Bump(); });
            }
        }

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
                    Children =
                    [
                        Caption($"Base URL: {baseHint}"),
                        HStack(8f,
                            ComboBox.Create(ApiDebugExecutor.MethodLabels, _method, width: 96f),
                            Editor(_url, 36f, placeholder: "/extended-metadata/v0/extended-metadata or https://…"),
                            Button.Accent(_busy.Value != 0 ? "Sending…" : "Send", Send, isEnabled: _busy.Value == 0)),
                        HStack(12f,
                            Field("Channel", ComboBox.Create(ApiDebugExecutor.ChannelLabels, _channel, width: 160f)),
                            Field("Response decode", ComboBox.Create(ApiDebugProto.DecodeLabels, _decode, width: 320f))),

                        Label("Headers  (Key: Value per line)"),
                        Editor(_headers, 80f, multiline: true),
                        BodySection(bodyMode, Bump),

                        Field("Request preview", Readout(_requestPreview)),
                        HStack(8f,
                            Button.Standard("Preview request", PreviewRequest),
                            Button.Standard("Copy response", () =>
                            {
                                if (_lastBody is { Length: > 0 })
                                    hooks.Clipboard?.SetText(ApiDebugProto.ExportJson(_lastBody, _decode.Peek(), _lastContentType));
                            }),
                            Button.Standard("Save JSON", SaveResponseJson),
                            Button.Standard("Save raw", SaveResponseRaw),
                            Button.Standard("Copy hex", () =>
                            {
                                if (_lastBody is { Length: > 0 })
                                    hooks.Clipboard?.SetText(ApiDebugProto.ToHex(ApiDebugProto.Decompress(_lastBody), int.MaxValue));
                            })),

                        Field("Status", Readout(_status)),
                        SelectorBar.Create(["Body", "Headers"], _responseTab, onChange: i => Bump()),
                        ResponsePane(),
                        Caption("gzip/zstd auto-decompressed · on-screen body truncated · Save JSON unpacks Artist/Track/Album protos"),
                    ],
                }) with { Grow = 1f, Shrink = 1f, MinHeight = 0f, ScrollKey = "api-console" },
            ],
        };

        Element ResponsePane() => new BoxEl
        {
            MinHeight = 240f, MaxHeight = 420f, ClipToBounds = true,
            Corners = CornerRadius4.All(6f), Fill = Tok.FillControlSecondary,
            BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
            Children =
            [
                ScrollView(new BoxEl
                {
                    Padding = Edges4.All(10f),
                    Children = [ Readout(_responseTab.Value == 0 ? _responseBody : _responseHeaders) ],
                }) with { Grow = 1f, MinHeight = 0f },
            ],
        };

        Element BodySection(int mode, Action bump) => mode switch
        {
            1 => VStack(8f,
                Label("Body"),
                SelectorBar.Create(ApiDebugBodyBuilder.BodyModeLabels, _bodyMode, OnBodyMode),
                Caption("JSON or plain text — enable gzip to compress automatically."),
                Editor(_body, 140f, multiline: true),
                ToggleRow("Gzip request body", _gzipText)),
            2 => VStack(8f,
                Label("Body"),
                SelectorBar.Create(ApiDebugBodyBuilder.BodyModeLabels, _bodyMode, OnBodyMode),
                Button.Standard("Fill extended-metadata headers", () =>
                {
                    _headers.Value = ApiDebugBodyBuilder.DefaultExtendedMetadataHeaders();
                    bump();
                }),
                Caption("One entity per line: uri | EXTENSION_KIND | optional_etag  (# comments OK; KIND accepts TRACK_V4 or TrackV4)"),
                ToggleRow("Bulk hydration (infer TrackV4/AlbumV4/… from URI only)", _bulkHydration),
                Editor(_body, 160f, multiline: true),
                HStack(8f,
                    Button.Standard("Preview body", PreviewRequest),
                    Button.Standard("Insert example", () =>
                    {
                        _body.Value = ApiDebugBodyBuilder.DefaultEntityLinesExample();
                        bump();
                    }))),
            _ => VStack(8f,
                Label("Body"),
                SelectorBar.Create(ApiDebugBodyBuilder.BodyModeLabels, _bodyMode, OnBodyMode),
                Caption("No request body.")),
        };
    }

    static Element PageHeader() => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
        Padding = new Edges4(Spacing.L, Spacing.L, Spacing.L, Spacing.M),
        Children =
        [
            Icon(Icons.Code, 22f, Tok.TextPrimary),
            WaveeType.PageHero("API Console") with { Grow = 1f },
        ],
    };

    static Element Field(string label, Element control) => VStack(4f, Label(label), control);

    static Element Label(string text) => new TextEl(text) { Size = 12f, Weight = 600, Color = Tok.TextSecondary };

    static Element Caption(string text) => new TextEl(text) { Size = 11f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap };

    static Element Readout(Signal<string> sig)
    {
        _ = sig.Value;
        return new TextEl(ApiDebugProto.ForDisplay(sig.Value)) { Size = 12f, Wrap = TextWrap.Wrap, Color = Tok.TextPrimary };
    }

    static Element Editor(Signal<string> sig, float height, bool multiline = false, string? placeholder = null)
        => Embed.Comp(() => new EditableText
        {
            Text = sig, Width = FieldW, Height = height,
            Placeholder = placeholder ?? "", AcceptsReturn = multiline,
        });

    static Element ToggleRow(string label, Signal<int> on) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = 8f,
        Children =
        [
            ToggleSwitch.Create(new Signal<bool>(on.Value != 0), onChange: _ => { on.Value = on.Peek() == 0 ? 1 : 0; }),
            new TextEl(label) { Size = 12f },
        ],
    };
}
