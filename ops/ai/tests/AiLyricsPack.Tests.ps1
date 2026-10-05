#requires -Version 5.1
<#
    Pester 3.4 (the version that ships with Windows PowerShell 5.1 - Describe / It / Should Be), matching
    ops/release/tests. Run with:

        Invoke-Pester -Path ops/ai/tests

    Covers ops/ai/lyrics-pack.v1.json (the AI lyrics pack manifest) and ops/ai/New-AiLyricsPackManifest.ps1.
    Offline: no network. The live bucket is checked by ops/ai/Test-AiLyricsPack.ps1 instead.
#>

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $here '..\..\..')).Path
$script:ManifestPath = Join-Path $repoRoot 'ops\ai\lyrics-pack.v1.json'
$script:GeneratorPath = Join-Path $repoRoot 'ops\ai\New-AiLyricsPackManifest.ps1'
$script:SmokePath = Join-Path $repoRoot 'ops\ai\Test-AiLyricsPack.ps1'
$script:GeneratedPath = Join-Path $repoRoot 'src\apps\Wavee\AiLyrics\AiLyrics.PackManifest.cs'

$script:HexPattern = '^[0-9a-f]{64}$'
$script:PartBytes = 64MB

$script:Pack = Get-Content -Raw -Path $script:ManifestPath | ConvertFrom-Json
$script:ModelFiles = @($script:Pack.common)
foreach ($lang in $script:Pack.languages.PSObject.Properties) { $script:ModelFiles += @($lang.Value.files) }

function Read-Normalized([string]$Path) {
    [System.IO.File]::ReadAllText($Path, (New-Object System.Text.UTF8Encoding($false))) -replace "`r`n", "`n"
}

function Get-EmbeddedJson([string]$CsText) {
    $m = [regex]::Match($CsText, '(?s)PackManifestJson = """\n(.*?)\n""";')
    if (-not $m.Success) { throw 'PackManifestJson raw string not found' }
    $m.Groups[1].Value
}

Describe 'lyrics-pack.v1.json' {

    It 'exists and is valid JSON' {
        Test-Path $script:ManifestPath | Should Be $true
        { Get-Content -Raw -Path $script:ManifestPath | ConvertFrom-Json } | Should Not Throw
    }

    It 'has a positive integer version that matches the file name' {
        ($script:Pack.version -is [int]) | Should Be $true
        $script:Pack.version | Should BeGreaterThan 0
        (Split-Path -Leaf $script:ManifestPath) | Should Be "lyrics-pack.v$($script:Pack.version).json"
    }

    It 'base is an https URL ending in "/" whose path is lyrics/v<version>/ (a new pack never reuses a path)' {
        $base = [string]$script:Pack.base
        $base | Should Match '^https://[^/]+/'
        $base.EndsWith('/') | Should Be $true
        $base.EndsWith("/lyrics/v$($script:Pack.version)/") | Should Be $true
    }

    It 'every runtime wheel has id, label, an https .whl url, bytes, a SHA-256 and something to extract' {
        @($script:Pack.runtime).Count | Should BeGreaterThan 0
        foreach ($w in $script:Pack.runtime) {
            [string]::IsNullOrEmpty($w.id) | Should Be $false
            [string]::IsNullOrEmpty($w.label) | Should Be $false
            $w.url | Should Match '^https://.+\.whl$'
            $w.bytes | Should BeGreaterThan 0
            $w.sha256 | Should Match $script:HexPattern
            ($null -ne $w.extract -or -not [string]::IsNullOrEmpty($w.extractDir)) | Should Be $true
        }
    }

    It 'has at least one language and every language lists files' {
        @($script:Pack.languages.PSObject.Properties).Count | Should BeGreaterThan 0
        foreach ($lang in $script:Pack.languages.PSObject.Properties) {
            $lang.Name | Should Match '^[a-z]{2}$'
            @($lang.Value.files).Count | Should BeGreaterThan 0
        }
    }

    It 'every model file has name, bytes, a SHA-256 and parts' {
        $script:ModelFiles.Count | Should BeGreaterThan 0
        foreach ($f in $script:ModelFiles) {
            [string]::IsNullOrEmpty($f.name) | Should Be $false
            $f.bytes | Should BeGreaterThan 0
            $f.sha256 | Should Match $script:HexPattern
            @($f.parts).Count | Should BeGreaterThan 0
        }
    }

    It 'every part has a path, bytes and a SHA-256, and the parts sum to the file size' {
        foreach ($f in $script:ModelFiles) {
            [long]$sum = 0
            foreach ($p in $f.parts) {
                [string]::IsNullOrEmpty($p.path) | Should Be $false
                $p.bytes | Should BeGreaterThan 0
                $p.sha256 | Should Match $script:HexPattern
                $sum += [long]$p.bytes
            }
            $sum | Should Be ([long]$f.bytes)
        }
    }

    It 'a single-part file is stored under its own name with the same hash' {
        foreach ($f in @($script:ModelFiles | Where-Object { @($_.parts).Count -eq 1 })) {
            $f.parts[0].path | Should Be $f.name
            $f.parts[0].sha256 | Should Be $f.sha256
            ([long]$f.bytes -le $script:PartBytes) | Should Be $true
        }
    }

    It 'a split file is <name>.part00, .part01, ... with every part but the last exactly 64 MiB' {
        foreach ($f in @($script:ModelFiles | Where-Object { @($_.parts).Count -gt 1 })) {
            $parts = @($f.parts)
            for ($i = 0; $i -lt $parts.Count; $i++) {
                $parts[$i].path | Should Be ('{0}.part{1:00}' -f $f.name, $i)
                if ($i -lt $parts.Count - 1) {
                    [long]$parts[$i].bytes | Should Be ([long]$script:PartBytes)
                } else {
                    ([long]$parts[$i].bytes -le $script:PartBytes) | Should Be $true
                }
            }
        }
    }

    It 'file names and object paths are unique and flat (no folders, no "..")' {
        $names = @($script:ModelFiles | ForEach-Object { $_.name })
        ($names | Select-Object -Unique).Count | Should Be $names.Count
        $paths = @($script:ModelFiles | ForEach-Object { $_.parts } | ForEach-Object { $_.path })
        ($paths | Select-Object -Unique).Count | Should Be $paths.Count
        foreach ($p in $paths) { $p | Should Match '^[A-Za-z0-9._-]+$'; $p.Contains('..') | Should Be $false }
    }
}

Describe 'New-AiLyricsPackManifest.ps1' {

    It 'parses as valid PowerShell (and so does Test-AiLyricsPack.ps1)' {
        foreach ($path in @($script:GeneratorPath, $script:SmokePath)) {
            $errors = $null
            [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$null, [ref]$errors) | Out-Null
            $errors.Count | Should Be 0
        }
    }

    It 'regenerates exactly the committed AiLyrics.PackManifest.cs' {
        $out = Join-Path $TestDrive 'AiLyrics.PackManifest.cs'
        & $script:GeneratorPath -Manifest $script:ManifestPath -Out $out 6>$null
        (Read-Normalized $out) | Should BeExactly (Read-Normalized $script:GeneratedPath)
    }

    It 'embeds JSON that parses to the same manifest' {
        $embedded = Get-EmbeddedJson (Read-Normalized $script:GeneratedPath)
        $a = $embedded | ConvertFrom-Json | ConvertTo-Json -Depth 20 -Compress
        $b = $script:Pack | ConvertTo-Json -Depth 20 -Compress
        $a | Should BeExactly $b
    }

    It 'is a no-op when run a second time' {
        $out = Join-Path $TestDrive 'twice.cs'
        & $script:GeneratorPath -Manifest $script:ManifestPath -Out $out 6>$null
        $first = [System.IO.File]::ReadAllBytes($out)
        $stamp = (Get-Item $out).LastWriteTimeUtc
        & $script:GeneratorPath -Manifest $script:ManifestPath -Out $out 6>$null
        [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($out)) | Should Be ([Convert]::ToBase64String($first))
        (Get-Item $out).LastWriteTimeUtc | Should Be $stamp
    }

    It 'refuses a manifest whose base is not https' {
        $bad = Join-Path $TestDrive 'lyrics-pack.v9.json'
        (Get-Content -Raw -Path $script:ManifestPath) -replace 'https://models', 'http://models' | Set-Content -Path $bad -Encoding UTF8
        { & $script:GeneratorPath -Manifest $bad -Out (Join-Path $TestDrive 'bad.cs') 6>$null } | Should Throw
    }
}
