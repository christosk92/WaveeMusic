using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Wavee.Analyzers.Baselines;

/// <summary>
/// Shared baseline infrastructure for the WVTH rules.
/// <para>
/// A rule's grandfathered violations live in an <c>AdditionalFiles</c> item named <c>WVTHnnn.txt</c> inside a directory
/// called <c>analyzer-baseline</c> (<c>src\apps\Wavee\analyzer-baseline\WVTH002.txt</c>). Format, one entry per line:
/// <code>
/// # comment lines and blank lines are ignored
/// Themes/Classic/Structure/LegacyPrefs.cs
/// Themes/Classic/Structure/LegacyPrefs.cs :: Prefs.Density
/// </code>
/// The path is a suffix of the offending file's path, matched on a segment boundary, either slash direction, case
/// insensitive. The optional ' :: detail' (the first ' :: ' splits) narrows the entry to diagnostics the rule reports with
/// that exact detail string; an entry without detail exempts every diagnostic of the rule in that file.
/// </para>
/// A rule calls <see cref="Register"/> from its compilation-start action, asks <see cref="BaselineTracker.IsExempt"/> per
/// finding, and the compilation-end action registered here reports WVTH900 for every entry that matched nothing (and
/// WVTH901 when the rule allows no baseline and a file exists anyway).
/// </summary>
public static class AnalyzerBaseline
{
    public const string DirectoryName = "analyzer-baseline";

    /// <summary>Loads the baseline for <paramref name="ruleId"/> and registers the compilation-end reporting.</summary>
    // RS1013 sees only this helper's end action; the calling rule registers its own actions on the same start context.
#pragma warning disable RS1013
    public static BaselineTracker Register(CompilationStartAnalysisContext context, string ruleId, bool allowsBaseline)
    {
        BaselineTracker tracker = Load(context.Options, ruleId, allowsBaseline, context.CancellationToken);
        context.RegisterCompilationEndAction(tracker.ReportCompilationEnd);
        return tracker;
    }
#pragma warning restore RS1013

    /// <summary>Loads without registering anything (for a rule that wants to drive the end action itself).</summary>
    public static BaselineTracker Load(AnalyzerOptions options, string ruleId, bool allowsBaseline, CancellationToken cancellationToken)
    {
        var files = new List<Location>();
        var entries = new List<BaselineEntry>();
        foreach (AdditionalText file in options.AdditionalFiles)
        {
            if (!IsBaselineFileFor(file.Path, ruleId))
                continue;
            files.Add(Location.Create(file.Path, default(TextSpan), default(LinePositionSpan)));
            if (!allowsBaseline)
                continue;
            SourceText? text = file.GetText(cancellationToken);
            if (text is null)
                continue;
            Parse(file.Path, text, entries);
        }
        return new BaselineTracker(ruleId, allowsBaseline, files.ToArray(), entries.ToArray());
    }

    /// <summary>True for <c>.../analyzer-baseline/WVTHnnn.txt</c> where the file name is exactly <c>{ruleId}.txt</c>.</summary>
    public static bool IsBaselineFileFor(string path, string ruleId)
    {
        string norm = BaselinePaths.Normalize(path);
        return string.Equals(BaselinePaths.FileName(norm), ruleId + ".txt", StringComparison.OrdinalIgnoreCase)
            && string.Equals(BaselinePaths.ParentSegment(norm), DirectoryName, StringComparison.OrdinalIgnoreCase);
    }

    private static void Parse(string filePath, SourceText text, List<BaselineEntry> into)
    {
        foreach (TextLine line in text.Lines)
        {
            string trimmed = line.ToString().Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
                continue;

            string pathPart = trimmed;
            string? detail = null;
            int sep = trimmed.IndexOf(" :: ", StringComparison.Ordinal);
            if (sep >= 0)
            {
                pathPart = trimmed.Substring(0, sep);
                string d = trimmed.Substring(sep + 4).Trim();
                detail = d.Length == 0 ? null : d;
            }

            string entryPath = BaselinePaths.NormalizeEntry(pathPart);
            if (entryPath.Length == 0)
                continue;

            Location location = Location.Create(filePath, line.Span, text.Lines.GetLinePositionSpan(line.Span));
            into.Add(new BaselineEntry(entryPath, detail, trimmed, location));
        }
    }
}

/// <summary>One parsed baseline line.</summary>
public sealed class BaselineEntry
{
    public BaselineEntry(string path, string? detail, string raw, Location location)
    {
        Path = path;
        Detail = detail;
        Raw = raw;
        Location = location;
    }

    /// <summary>Normalised path suffix (forward slashes).</summary>
    public string Path { get; }

    /// <summary>The text after ' :: ', or null when the entry exempts the whole file.</summary>
    public string? Detail { get; }

    /// <summary>The trimmed line as written, for diagnostics.</summary>
    public string Raw { get; }

    /// <summary>The line inside the baseline file.</summary>
    public Location Location { get; }
}

/// <summary>Per-compilation, thread-safe view of one rule's baseline. Records which entries were consulted successfully so
/// the compilation-end action can report the rest as stale.</summary>
public sealed class BaselineTracker
{
    private readonly string _ruleId;
    private readonly bool _allowsBaseline;
    private readonly Location[] _files;
    private readonly BaselineEntry[] _entries;
    private readonly int[] _matched;

    internal BaselineTracker(string ruleId, bool allowsBaseline, Location[] files, BaselineEntry[] entries)
    {
        _ruleId = ruleId;
        _allowsBaseline = allowsBaseline;
        _files = files;
        _entries = entries;
        _matched = new int[entries.Length];
    }

    public string RuleId => _ruleId;

    /// <summary>The number of parsed entries (zero when the rule allows no baseline).</summary>
    public int EntryCount => _entries.Length;

    /// <summary>True when the baseline exempts a finding at <paramref name="location"/>. <paramref name="detail"/> is the
    /// rule's own discriminator (a member name, a literal...); entries with a detail only match an equal string. Marks every
    /// matching entry as used.</summary>
    public bool IsExempt(Location location, string? detail = null)
    {
        if (_entries.Length == 0)
            return false;
        string? path = location.SourceTree?.FilePath;
        if (path is null || path.Length == 0)
            return false;
        string norm = BaselinePaths.Normalize(path);

        bool exempt = false;
        for (int i = 0; i < _entries.Length; i++)
        {
            BaselineEntry e = _entries[i];
            if (!BaselinePaths.EndsWithPath(norm, e.Path))
                continue;
            if (e.Detail is not null && !string.Equals(e.Detail, detail, StringComparison.Ordinal))
                continue;
            Interlocked.Exchange(ref _matched[i], 1);
            exempt = true; // keep going: every entry that applies counts as used, so none is reported stale
        }
        return exempt;
    }

    internal void ReportCompilationEnd(CompilationAnalysisContext context)
    {
        if (!_allowsBaseline)
        {
            foreach (Location file in _files)
            {
                string name = BaselinePaths.FileName(BaselinePaths.Normalize(file.GetLineSpan().Path));
                context.ReportDiagnostic(Diagnostic.Create(BaselineDiagnostics.DisallowedBaseline, file, _ruleId, name));
            }
            return;
        }

        for (int i = 0; i < _entries.Length; i++)
        {
            if (Volatile.Read(ref _matched[i]) != 0)
                continue;
            BaselineEntry e = _entries[i];
            string name = BaselinePaths.FileName(BaselinePaths.Normalize(e.Location.GetLineSpan().Path));
            context.ReportDiagnostic(Diagnostic.Create(BaselineDiagnostics.StaleEntry, e.Location, _ruleId, e.Raw, name));
        }
    }
}
