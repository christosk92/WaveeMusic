#Requires -Version 5.1
<#
    Generates src/apps/Wavee/AiLyrics/AiLyrics.PackManifest.cs from the AI lyrics pack manifest
    (ops/ai/lyrics-pack.v1.json), which is the single source of truth for every file the AI lyrics feature
    downloads: its size, its SHA-256 and, for model files, its 64 MiB parts on models.cproducts.dev.

    The JSON is embedded verbatim (CRLF line endings, outer whitespace trimmed) as a C# raw string, so the
    compiled-in manifest is exactly the reviewed file. The output is deterministic: running the script twice
    is a no-op, and it does not rewrite the .cs when only its line endings differ.
    Docs: docs/guide/ai-lyrics-pack.md. Tests: Invoke-Pester -Path ops/ai/tests

    Usage:
        powershell -File ops/ai/New-AiLyricsPackManifest.ps1
        powershell -File ops/ai/New-AiLyricsPackManifest.ps1 -Manifest ops/ai/lyrics-pack.v1.json -Out src/apps/Wavee/AiLyrics/AiLyrics.PackManifest.cs
#>

param(
    [string]$Manifest = (Join-Path $PSScriptRoot 'lyrics-pack.v1.json'),
    [string]$Out = (Join-Path $PSScriptRoot '..\..\src\apps\Wavee\AiLyrics\AiLyrics.PackManifest.cs')
)

$ErrorActionPreference = 'Stop'

$manifestPath = (Resolve-Path -LiteralPath $Manifest).Path
$outPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Out)
$utf8 = New-Object System.Text.UTF8Encoding($false)

# ReadAllText drops a UTF-8 BOM if there is one.
$json = [System.IO.File]::ReadAllText($manifestPath, $utf8).Trim()
$json = ($json -replace "`r`n", "`n") -replace "`n", "`r`n"

try {
    $parsed = $json | ConvertFrom-Json
} catch {
    throw "$manifestPath is not valid JSON: $($_.Exception.Message)"
}
if (-not ($parsed.version -is [int]) -or $parsed.version -lt 1) {
    throw "$manifestPath`: 'version' must be a positive integer."
}
$baseUri = $null
if (-not [Uri]::TryCreate([string]$parsed.base, [UriKind]::Absolute, [ref]$baseUri) -or $baseUri.Scheme -ne 'https' -or -not ([string]$parsed.base).EndsWith('/')) {
    throw "$manifestPath`: 'base' must be an https URL ending in '/' (got '$($parsed.base)')."
}
if ($json.Contains('"""')) {
    throw "$manifestPath contains three quotes in a row, which would end the C# raw string literal."
}

# Kept ASCII-only: Windows PowerShell 5.1 reads a BOM-less .ps1 in the ANSI code page.
$rule = [string][char]0x2500
$dash = [string][char]0x2014
$title = '// ' + ($rule * 2) + ' AiLyrics/AiLyrics.PackManifest.cs '
$title += $rule * (119 - $title.Length)
$host_ = $baseUri.Host
$path_ = $baseUri.AbsolutePath.TrimStart('/')
$source = 'ops/ai/' + (Split-Path -Leaf $manifestPath)

$lines = @(
    $title
    "// GENERATED from $source by ops/ai/New-AiLyricsPackManifest.ps1 $dash do not edit by hand."
    '//'
    '// Every file the AI lyrics feature downloads, with its size and SHA-256. Compiled into the signed app on purpose: a'
    '// file changed on a server can never be installed; new models ship with an app update (plan ' + [char]0x00A7 + '5). Models are served'
    "// from $host_ (R2 bucket wavee-ai-models, immutable path $path_, split into 64 MiB parts); the"
    '// runtime comes from the official PyPI wheels.'
    ''
    'namespace Wavee;'
    ''
    'public static partial class AiLyrics'
    '{'
    '    public const string PackManifestJson = """'
    $json
    '""";'
    '}'
    ''
)
$text = $lines -join "`r`n"

$dir = Split-Path -Parent $outPath
if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

if (Test-Path -LiteralPath $outPath) {
    $existing = [System.IO.File]::ReadAllText($outPath, $utf8) -replace "`r`n", "`n"
    if ($existing -eq ($text -replace "`r`n", "`n")) {
        Write-Host "up to date: $outPath"
        return
    }
}
[System.IO.File]::WriteAllText($outPath, $text, $utf8)
Write-Host "wrote $outPath (from $source, pack version $($parsed.version))"
