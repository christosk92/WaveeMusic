using System;
using System.Collections.Generic;
using System.Reflection;
using Wavee.Core;

namespace Wavee;

/// <summary>
/// The one place the app answers "what build am I?". Reads <see cref="AssemblyInformationalVersionAttribute"/> plus
/// every <see cref="AssemblyMetadataAttribute"/> stamped on the Wavee assembly by <c>Wavee.csproj</c> /
/// <c>ops/build/pack-wavee-msix.ps1</c> (Codename, Channel, PackageVersion, Commit, BuildDate, FeedRelease,
/// UpdateBaseUrl) and hands them to the pure <see cref="WaveeVersionInfo.Parse"/>.
/// <para>
/// Reflection happens ONCE, in a static initializer, and nothing downstream reflects again: About, the crash header,
/// the diagnostics page, the update feed URL and the GitHub user-agent all read <see cref="Info"/>. A build with no
/// attributes at all (a headless test host) degrades to a dev build — never to something that looks shippable.
/// </para>
/// </summary>
static class AppVersion
{
    /// <summary>Everything this build knows about itself. Parsed once; never null.</summary>
    public static WaveeVersionInfo Info { get; } = Resolve();

    /// <summary>The running build's semver — e.g. <c>0.2.0-dev</c> or <c>0.2.0</c>. Shorthand for <c>Info.SemVer</c>.</summary>
    public static string Current => Info.SemVer;

    /// <summary>True for a build Windows never installed (no MSIX quad, or an explicitly <c>dev</c> channel): the
    /// update checker refuses to prompt it, and About captions it so a screenshot is never mistaken for a release.</summary>
    public static bool IsDev => Info.IsDev;

    static WaveeVersionInfo Resolve()
    {
        WaveeVersionInfo info;
        try
        {
            var asm = typeof(AppVersion).Assembly;
            string? inf = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var a in asm.GetCustomAttributes<AssemblyMetadataAttribute>())
                if (a.Key is { Length: > 0 } k && a.Value is { } v)
                    metadata[k] = v;
            info = WaveeVersionInfo.Parse(inf, metadata);
        }
        catch (Exception ex)
        {
            // A build stamp is never worth failing startup over: an unreadable assembly reports itself as dev, which
            // is the safest possible answer (no update prompt, no version claim). It is still a REAL fault and must
            // not be silent — a packaged build that degrades to dev stops offering updates, and the only symptom the
            // user ever sees is "Check for updates" being greyed out forever.
            Warn("build stamp unreadable; reporting this build as dev", ex);
            info = WaveeVersionInfo.Parse(null, new Dictionary<string, string>(StringComparer.Ordinal));
        }

        // The same silence, reached the other way: the assembly read fine but carried no PackageVersion/Channel, while
        // Windows says we ARE an installed package. That is a packaging bug (a pack script that skipped the stamp),
        // and it disables the whole updater.
        if (info.IsDev && IsPackagedQuiet())
            Warn("running packaged but the build carries no version stamp (channel '" + info.Channel
                 + "', quad '" + info.Quad + "') — the updater is disabled for this build", null);

        return info;
    }

    /// <summary>Best-effort <c>WaveeLog</c>, falling back to stderr. This runs inside a STATIC INITIALIZER, before the
    /// composition root exists, so the log may not be constructible yet (and constructing it must not be what fails).</summary>
    static void Warn(string message, Exception? ex)
    {
        try { WaveeLog.Instance.Warn("app", message, ex); }
        catch (Exception)
        {
            try { Console.Error.WriteLine("[app] " + message + (ex is null ? "" : " :: " + ex)); }
            catch (Exception) { }
        }
    }

    /// <summary>"Is this an MSIX package?" without letting the probe itself throw into a static initializer.</summary>
    static bool IsPackagedQuiet()
    {
        try { return FluentGpu.WindowsApi.Packaging.PackageIdentity.IsPackaged; }
        catch (Exception) { return false; }
    }
}

/// <summary>
/// The build's name as a SENTENCE the UI prints: "Wavee 0.3.0 “Crest”", "Wavee 0.4.0 “Drift” · Beta 2", or plain
/// "Wavee 0.3.0" when the codename was never stamped.
///
/// <para>Localized, unlike <see cref="WaveeVersionInfo.Display"/>. The record stays pure (it is also what crash
/// headers and copied diagnostics print, where a culture table is not available and a stable shape is the point);
/// this is the same three shapes as loc keys, so a translation can put the quotes, the separator and the word "Beta"
/// where its own typography wants them. Used by Settings › About's hero and the after-update plate.</para>
/// </summary>
static class AppVersionDisplay
{
    public static string Of(WaveeVersionInfo? me)
    {
        if (me is null) return "";
        string version = me.IsDev ? me.SemVer : me.Core;
        if (me.Codename is not { Length: > 0 } name) return Strings.Update.About.DisplayBare(version);
        return me.Beta is int beta
            ? Strings.Update.About.DisplayBeta(version, name, beta)
            : Strings.Update.About.Display(version, name);
    }
}
