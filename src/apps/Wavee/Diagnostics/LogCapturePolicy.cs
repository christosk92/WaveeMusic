using System;

namespace Wavee;

// The Verbose toggle + the two capture-level dropdowns' decision core — engine-free (System + IAppSettings/WaveeLog
// only) so LogCapturePolicyTests can pin it directly. CLAUDE.md forbids env-var behaviour switches: WAVEE_LOG_LEVEL /
// WAVEE_LOG_FILE_LEVEL are deleted (WaveeLog.cs), and this is the ONE place that resolves a persisted level setting
// (-1 = build default) against the build's own default — Program.cs at startup and LogsPanel at runtime both call
// through here so the two can never disagree about what "-1" means.
static class LogCapturePolicy
{
    /// <summary>The ring/live-view default: full Debug detail in a Debug build (a dev inner-loop wants everything),
    /// Info in Release (matches the shipped file default below).</summary>
    public static WaveeLogLevel BuildDefaultMinLevel =>
#if DEBUG
        WaveeLogLevel.Debug;
#else
        WaveeLogLevel.Info;
#endif

    /// <summary>The file sink's default in every build — a Debug run keeping full Debug detail in the RING still
    /// defaults its FILE to Info, so a dev run doesn't bloat wavee.log with the demoted verbose flow.</summary>
    public const WaveeLogLevel BuildDefaultFileLevel = WaveeLogLevel.Info;

    /// <summary>Persisted setting → effective level: -1 (never chosen, or explicitly reset) resolves to the build
    /// default; anything else clamps into the user-selectable Trace..Error range (Critical is never offered — see
    /// <see cref="LogView.LevelNames"/>).</summary>
    public static WaveeLogLevel Resolve(int setting, WaveeLogLevel buildDefault) =>
        setting < 0 ? buildDefault : (WaveeLogLevel)Math.Clamp(setting, (int)WaveeLogLevel.Trace, (int)WaveeLogLevel.Error);

    /// <summary>Effective level → the value persisted: a level that IS the build default is stored as -1 (so a build
    /// whose default later changes carries an existing install's "never touched this" choice forward correctly)
    /// rather than freezing today's default as an explicit setting.</summary>
    public static int ToSetting(WaveeLogLevel level, WaveeLogLevel buildDefault) =>
        level == buildDefault ? -1 : (int)level;

    /// <summary>Verbose is "on" exactly when the ring is capturing Debug or below — the single primary toggle's
    /// checked state derives from the live level rather than a separate persisted bool, so it can never drift from
    /// what the capture-level submenu independently shows.</summary>
    public static bool IsVerbose(WaveeLogLevel min) => min <= WaveeLogLevel.Debug;

    /// <summary>What flipping Verbose sets MinLevel to: Trace when turning it on (the deepest level there is), the
    /// build default when turning it off (never a fixed Info/Debug — a Debug build's "off" is still Debug).</summary>
    public static WaveeLogLevel VerboseTarget(bool on, WaveeLogLevel buildDefault) => on ? WaveeLogLevel.Trace : buildDefault;

    /// <summary>WaveeLog's own upward-only file rule (WaveeLog.cs: "a line reaches the file when its level is >= both
    /// MinLevel and FileMinLevel"), exposed here so the footer caption and the File log level submenu can show what
    /// ACTUALLY reaches disk — lowering the file level below the ring's own MinLevel has no effect.</summary>
    public static WaveeLogLevel EffectiveFileLevel(WaveeLogLevel min, WaveeLogLevel file) => file < min ? min : file;

    /// <summary>Apply a new ring/live MinLevel to the running logger AND persist it — the panel's only writer for
    /// this setting, so every entry point (the Verbose toggle, the Capture level radios) goes through one place.</summary>
    public static void SetMinLevel(WaveeLog log, IAppSettings? settings, WaveeLogLevel level, WaveeLogLevel buildDefault)
    {
        log.MinLevel = level;
        settings?.Set(WaveeSettings.LogMinLevel, ToSetting(level, buildDefault));
    }

    /// <summary>Apply a new file MinLevel to the running logger AND persist it (File log level radios).</summary>
    public static void SetFileLevel(WaveeLog log, IAppSettings? settings, WaveeLogLevel level)
    {
        log.FileMinLevel = level;
        settings?.Set(WaveeSettings.LogFileMinLevel, ToSetting(level, BuildDefaultFileLevel));
    }

    /// <summary>Flip the Verbose toggle: routes through <see cref="SetMinLevel"/> so the persisted setting and the
    /// live logger move together, same as every other level change here.</summary>
    public static void SetVerbose(WaveeLog log, IAppSettings? settings, bool on, WaveeLogLevel buildDefault) =>
        SetMinLevel(log, settings, VerboseTarget(on, buildDefault), buildDefault);
}
