using System;
using System.IO;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>Cross-tab settings helpers shared by <see cref="SettingsPage"/> and <see cref="LogsPanel"/>.</summary>
static class SettingsShared
{
    public static string AppDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wavee");

    public static void OpenFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path)) path = Path.GetDirectoryName(path) ?? path;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + path + "\"")
            { UseShellExecute = false });
        }
        catch { /* best-effort — a missing Explorer/path must not throw into the UI */ }
    }

    public static void Confirm(IOverlayService? overlay, string title, string body, string primaryText, Action onConfirm)
    {
        if (overlay is null) { onConfirm(); return; }
        ContentDialog.Show(overlay, d =>
        {
            d.Title = title;
            d.Message = body;
            d.PrimaryText = primaryText;
            d.CloseText = Loc.Get(Strings.Auth.Cancel);
            d.DefaultButton = ContentDialog.DefaultBtn.Close;
            d.PrimaryClick = onConfirm;
        });
    }

    /// <summary>The shared "this surface is still fetching" block — a ring over a centred, growing box. Used by the
    /// What's-new page while its document loads; anything else that waits on I/O should use the same one rather than
    /// inventing a third spinner.</summary>
    public static Element Loading() => new BoxEl
    {
        Grow = 1f, Shrink = 1f, MinHeight = 0f, Direction = 1,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Children = [ ProgressRing.Create(size: 28f) ],
    };
}

/// <summary>The two hooks the settings tabs need and the engine does not ship.
///
/// <para>Extension methods on <see cref="Component"/> (not helpers in <see cref="SettingsShared"/>) because the hook
/// surface is <c>protected</c> on the component but <c>public</c> on its <see cref="RenderContext"/> — and hook
/// identity is (file, line, ORDINAL), so calling one of these from several sites in one render is safe exactly as long
/// as the call ORDER is stable, which is the ordinary hook rule.</para></summary>
static class SettingsHooks
{
    /// <summary>Subscribe an <c>IObservable&lt;int&gt;</c> revision tick and re-render on every publish. Returns the
    /// local revision (read it so the render subscribes). The service may publish from a worker thread, so the write is
    /// marshalled through the host poster — <c>HostDispatch.Current</c> is null only headlessly, where inline IS the UI
    /// thread.</summary>
    public static int UseObservable(this Component c, IObservable<int>? source)
    {
        var rev = c.Context.UseSignal(0);
        c.Context.UseEffect(() =>
        {
            if (source is null) return null;
            var sub = source.Subscribe(_ =>
            {
                void Bump() => rev.Value = rev.Peek() + 1;
                var post = HostDispatch.Current;
                if (post is null) Bump(); else post(Bump);
            });
            return sub.Dispose;
        }, DepKey.Empty);
        return rev.Value;
    }

    /// <summary>A stable per-call-site <see cref="Signal{T}"/> seeded ONCE from a persisted setting — the shape every
    /// FluentGpu input control wants (it freezes the signal instance at mount and writes it on interaction, then calls
    /// the caller's <c>onChange</c>, which is where the value is persisted). A fresh signal per render would be
    /// discarded by the control's frozen prop and the row would never move.</summary>
    public static Signal<T> UseSettingSignal<T>(this Component c, IAppSettings? settings, SettingKey<T> key)
        => c.Context.UseSignal(settings is null ? key.Default : settings.Get(key));
}
