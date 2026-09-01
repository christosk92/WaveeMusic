using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core.ReleaseNotes;

namespace Wavee.ReleaseTool;

/// <summary>
/// <c>wavee-release</c> — the release-notes validator and emitter. Invoked by
/// <c>ops\release\wavee-release.ps1</c> (phase 2) as <c>dotnet run --project src\apps\Wavee.ReleaseTool -- validate …</c>,
/// and by hand for the <c>render</c> preview.
/// </summary>
/// <remarks>
/// Exit codes are the script's contract: <c>0</c> ok, <c>2</c> the release is not shippable (validation), <c>1</c>
/// usage or I/O. Nothing is written unless validation passes, so a failed run leaves the output folder untouched.
/// </remarks>
static class Program
{
    static async Task<int> Main(string[] argv)
    {
        var a = Args.Parse(argv);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        if (a.Flag("help") || a.Flag("h")) { PrintUsage(); return Validator.ExitOk; }

        try
        {
            switch (a.Command.ToLowerInvariant())
            {
                case "validate":
                {
                    var bad = a.UnknownKeys("semver", "quad", "codename", "channel", "changelog", "notes", "out",
                                            "repo", "previous-index", "previous-tag", "github-token",
                                            "allow-unresolved", "commits", "allow-unlinked");
                    if (bad.Count > 0) { Console.Error.WriteLine("error: unknown option(s): " + string.Join(", ", bad)); PrintUsage(); return Validator.ExitUsage; }
                    return await Validator.RunAsync(a, cts.Token).ConfigureAwait(false);
                }
                case "render":
                    return Render(a);
                case "help" or "--help" or "-h" or "":
                    PrintUsage();
                    return a.Command.Length == 0 ? Validator.ExitUsage : Validator.ExitOk;
                default:
                    Console.Error.WriteLine("error: unknown command '" + a.Command + "'");
                    PrintUsage();
                    return Validator.ExitUsage;
            }
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("error: cancelled");
            return Validator.ExitUsage;
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return Validator.ExitUsage;
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return Validator.ExitUsage;
        }
    }

    /// <summary>
    /// <c>render</c> — a developer convenience: print the release body (or the store listing) for an already
    /// emitted <c>whatsnew.json</c>, so the wording can be iterated on without re-running validation.
    /// </summary>
    static int Render(Args a)
    {
        var bad = a.UnknownKeys("notes", "repo", "markdown", "store-listing");
        if (bad.Count > 0) { Console.Error.WriteLine("error: unknown option(s): " + string.Join(", ", bad)); PrintUsage(); return Validator.ExitUsage; }

        string? path = a.Get("notes");
        if (path is null) { Console.Error.WriteLine("error: render needs --notes <whatsnew.json>"); PrintUsage(); return Validator.ExitUsage; }
        if (Directory.Exists(path)) path = Path.Combine(path, "whatsnew.json");
        if (!File.Exists(path)) { Console.Error.WriteLine("error: not found: " + path); return Validator.ExitUsage; }

        ReleaseNotesDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize(File.ReadAllBytes(path), ReleaseNotesJsonContext.Default.ReleaseNotesDocument);
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine("error: " + path + " is not valid JSON: " + ex.Message);
            return Validator.ExitInvalid;
        }
        if (doc is null) { Console.Error.WriteLine("error: " + path + " deserialized to nothing"); return Validator.ExitInvalid; }
        // An explicit null anywhere on the wire would otherwise NRE in the renderer.
        doc.Normalize();

        string repo = a.GetOr("repo", "christosk92/WaveeMusic");
        Console.Out.Write(a.Flag("store-listing")
            ? ReleaseNotesValidation.RenderStoreListing(doc)
            : ReleaseNotesValidation.RenderBody(doc, repo, generatedNotes: null));
        Console.Out.Write('\n');
        return Validator.ExitOk;
    }

    /// <summary>The usage text; also printed on every usage error.</summary>
    public static void PrintUsage() => Console.Error.WriteLine(Usage);

    const string Usage = """
        wavee-release — validate and emit Wavee release notes

        USAGE
          dotnet run --project src/apps/Wavee.ReleaseTool -- <command> [options]

        COMMANDS
          validate    Check CHANGELOG.md + ops/release/wavee/<semver>/whatsnew.json against each other and
                      against GitHub, then emit the release artefacts into --out.
          render      Print the release body for an already emitted whatsnew.json (no network, no writes).
          help        This text.

        VALIDATE OPTIONS
          --semver <M.m.p>          Required. The version being released; must match whatsnew.json and CHANGELOG.md.
          --quad <M.m.p.B>          Required. The MSIX Identity/@Version stamped into the emitted whatsnew.json.
          --codename <name>         Required. Must match whatsnew.json "name".
          --channel <stable|beta>   Required.
          --changelog <file>        Required. Path to CHANGELOG.md.
          --notes <dir>             Required. The authored folder (whatsnew.json + media/).
          --out <dir>               Required. Where the emitted artefacts are written.
          --repo <owner/name>       Required. Default repo for bare #123 refs, and the source of every link.
          --previous-index <file>   Optional. The whatsnew-index.json from the rolling feed release; this release
                                    is prepended to it (newest first, capped at 12).
          --previous-tag <tag>      Optional. Fills links.compare and generate-notes' previous_tag_name.
          --commits <file>          Required with --previous-tag. The commits.json the script writes from
                                    `git log <previous-tag>..HEAD` — cross-checked against the CHANGELOG's
                                    (#n) refs; mismatches fail the release (see --allow-unlinked).
          --github-token <token>    Optional. Raises the issue-lookup rate limit and enables the commits and
                                    contributors appendix (POST /releases/generate-notes). Falls back to the
                                    GITHUB_TOKEN environment variable, which is how the release script passes it.
                                    Without either, lookups still run unauthenticated (60/hour).
          --allow-unresolved        Optional. Ship even though some referenced issue/PR could not be read from
                                    GitHub (throttled, forbidden, offline). By default that is exit 2: the
                                    authored title/state would otherwise be published as if it had been verified.
          --allow-unlinked          Optional. Ship although git and CHANGELOG.md disagree about which issues were
                                    fixed (a commit fixes an issue the CHANGELOG entry doesn't cite, or vice versa).
                                    By default that is exit 2; mismatches ship as warnings instead.

        RENDER OPTIONS
          --notes <file|dir>        Required. An emitted whatsnew.json (or the folder holding one).
          --repo <owner/name>       Optional. Default christosk92/WaveeMusic.
          --markdown                Print RELEASE_BODY.md to stdout (the default).
          --store-listing           Print store-listing.txt to stdout instead.

        OUTPUTS (validate)
          <out>/whatsnew.json       The authored document merged with the CHANGELOG sections, the quad, the
                                    channel, the date, the links, the media hashes and generatedAt.
          <out>/whatsnew-index.json The release index, newest first, capped at 12.
          <out>/RELEASE_BODY.md     The GitHub release body.
          <out>/store-listing.txt   Tagline + highlight titles, capped at 1500 characters.
          <out>/media/*             Every referenced media file, flat, by basename.

        EXIT CODES
          0  ok
          1  usage or I/O error
          2  validation failed (nothing was written)

        EXAMPLE
          dotnet run --project src/apps/Wavee.ReleaseTool -- validate ^
            --semver 0.2.0 --quad 0.2.0.17 --codename Breaker --channel stable ^
            --changelog CHANGELOG.md --notes ops/release/wavee/0.2.0 ^
            --out artifacts/release/0.2.0/notes --repo christosk92/WaveeMusic ^
            --previous-tag wavee-v0.1.2 --commits artifacts/release/0.2.0/commits.json --github-token $env:GITHUB_TOKEN
        """;
}
