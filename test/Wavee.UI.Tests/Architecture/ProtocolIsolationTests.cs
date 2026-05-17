using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace Wavee.UI.Tests.Architecture;

/// <summary>
/// Phase 3 lockdown of the protocol-isolation invariant: ViewModels and Controls
/// in <c>Wavee.UI.WinUI</c> must not reference the raw Spotify protocol surface.
/// Every protocol touch lives behind a <c>Wavee.UI/Services</c> interface (or the
/// thin WinUI-side service adapter that wraps one).
///
/// <para>Implementation: scans <c>src/Wavee.UI.WinUI/ViewModels</c> and
/// <c>src/Wavee.UI.WinUI/Controls</c> as source text, after stripping <c>//</c>
/// line comments and <c>/* ... */</c> block comments. Flags any <c>using</c>
/// directive that imports a banned namespace, and any fully-qualified reference
/// (<c>Wavee.Core.Session.X</c> etc.) outside an allow-listed file.</para>
///
/// <para>The allow-list is intentionally small. It covers diagnostics surfaces
/// (Debug page + dealer-feed viewer), the settings page's NTP-clock readout
/// (touches <c>session.Clock</c>), and the one Artist VM call site that still
/// needs <c>SessionException</c> to drive a "Connecting to Spotify…" UX
/// transition. Everything else has been drained to service interfaces.</para>
/// </summary>
public class ProtocolIsolationTests
{
    private static readonly string[] BannedNamespaces =
    [
        "Wavee.Core.Session",
        "Wavee.Core.Http.SpClient",
        "Wavee.Core.Http.IPathfinderClient",
        "Wavee.Core.Http.ISpClient",
        "Wavee.Protocol",
        "Wavee.Connect",
        "Wavee.Core.Authentication",
        "Wavee.Mercury",
    ];

    // Allow-list of files (relative to repo root) that may legitimately
    // reference banned namespaces. Keep small and well-justified.
    private static readonly HashSet<string> AllowList = new(StringComparer.OrdinalIgnoreCase)
    {
        // Debug + diagnostics: the Debug page is the raw HTTP / dealer / Pathfinder
        // test bench; it needs ISession + SpClient + Pathfinder + Mercury + Dealer.
        @"src\Wavee.UI.WinUI\ViewModels\DebugViewModel.cs",
        // ConnectStateViewModel renders the raw RemoteStateRecorder dealer feed.
        @"src\Wavee.UI.WinUI\ViewModels\ConnectStateViewModel.cs",
        // SettingsViewModel exposes a session-clock NTP-style debug readout.
        @"src\Wavee.UI.WinUI\ViewModels\SettingsViewModel.cs",
        // ArtistViewModel catches SessionException to drive the "Connecting…"
        // hero state; the catch is fully-qualified to advertise the exemption.
        @"src\Wavee.UI.WinUI\ViewModels\ArtistViewModel.cs",
        // AudioOutputPicker maps Wavee.Connect's DeviceType enum to glyphs.
        // The enum is a wire-shape but used here as a domain enum.
        @"src\Wavee.UI.WinUI\Controls\Playback\AudioOutputPicker.xaml.cs",
        // LinkSpotifyTrackFlyout writes a Spotify track metadata blob onto a
        // local file's overlay — it constructs the protobuf shape directly.
        @"src\Wavee.UI.WinUI\Controls\Local\LinkSpotifyTrackFlyout.xaml.cs",
        // LocalItemContextMenuBuilder reads metadata-blob fields when building
        // the right-click menu for a locally-linked Spotify track.
        @"src\Wavee.UI.WinUI\Controls\ContextMenu\Builders\LocalItemContextMenuBuilder.cs",
    };

    [Fact]
    public void ViewModels_and_Controls_must_not_reference_banned_namespaces()
    {
        var repoRoot = LocateRepoRoot();
        var roots = new[]
        {
            Path.Combine(repoRoot, "src", "Wavee.UI.WinUI", "ViewModels"),
            Path.Combine(repoRoot, "src", "Wavee.UI.WinUI", "Controls"),
        };

        var violations = new List<string>();

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;

            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(repoRoot, file);

                if (AllowList.Contains(relative))
                    continue;

                var text = File.ReadAllText(file);
                var stripped = StripComments(text);

                foreach (var banned in BannedNamespaces)
                {
                    // `using Wavee.Core.Session;` or `using static Wavee.Core.Session.X;`
                    if (Regex.IsMatch(stripped, $@"^\s*using\s+(static\s+)?{Regex.Escape(banned)}\b", RegexOptions.Multiline))
                    {
                        violations.Add($"{relative}: using {banned}");
                        continue;
                    }

                    // Fully-qualified reference like `Wavee.Core.Session.X`
                    if (Regex.IsMatch(stripped, $@"(?<![A-Za-z0-9_\.]){Regex.Escape(banned)}\.\w"))
                    {
                        violations.Add($"{relative}: fully-qualified reference to {banned}");
                    }
                }
            }
        }

        violations.Should().BeEmpty(
            "ViewModels and Controls in Wavee.UI.WinUI must consume Wavee.UI service interfaces, not the raw protocol layer:\n  - "
            + string.Join("\n  - ", violations));
    }

    private static string StripComments(string source)
    {
        // Remove /* block */ comments first (greedy across newlines), then // line comments.
        // Doesn't try to be perfect about strings — banned-namespace literals inside a string
        // are vanishingly rare and would surface as a separate violation anyway.
        var noBlock = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        var sb = new StringBuilder(noBlock.Length);
        foreach (var line in noBlock.Split('\n'))
        {
            var idx = line.IndexOf("//", StringComparison.Ordinal);
            sb.AppendLine(idx >= 0 ? line[..idx] : line);
        }
        return sb.ToString();
    }

    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            // Sentinel: the solution-explorer XML lives at repo root.
            if (File.Exists(Path.Combine(dir.FullName, "Wavee.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            $"Could not locate repo root (Wavee.slnx) walking up from {AppContext.BaseDirectory}");
    }
}
