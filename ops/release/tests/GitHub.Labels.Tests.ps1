#requires -Version 5.1
<#
    Pester 3.4 (the version that ships with Windows PowerShell 5.1 - Describe / Context / It / Should Be /
    Should Throw), matching Wavee.Store.Tests.ps1 in this folder. Run with:

        Invoke-Pester -Path ops/release/tests/GitHub.Labels.Tests.ps1

    Covers ops/github/labels.json and ops/github/sync-labels.ps1. Pure: no `gh` calls, no network. These
    only validate the shape of the checked-in label set (unique names, valid hex colors, the naming
    convention, the renameFrom bookkeeping for the seven GitHub stock labels) and that sync-labels.ps1
    is syntactically valid PowerShell.
#>

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $here '..\..\..')).Path
$script:LabelsPath = Join-Path $repoRoot 'ops\github\labels.json'
$script:SyncScriptPath = Join-Path $repoRoot 'ops\github\sync-labels.ps1'

$script:StockRenameFroms = @('bug', 'enhancement', 'documentation', 'question', 'duplicate', 'wontfix', 'invalid')
$script:NamePattern = '^(type|area|arch|install|status|resolution): [a-z0-9-]+$'
$script:PriorityPattern = '^P[0-3]: (critical|high|medium|low)$'

Describe 'labels.json' {

    It 'exists and is valid JSON' {
        Test-Path $script:LabelsPath | Should Be $true
        { Get-Content -Raw -Path $script:LabelsPath | ConvertFrom-Json } | Should Not Throw
    }

    # ConvertFrom-Json emits a JSON array as a single pipeline object that is itself an array, so a plain
    # assignment unrolls it correctly (53 entries) - wrapping the pipeline in @(...) here would instead
    # collect that one emitted object into a 1-element array-of-array. Do not add @() around this.
    $script:Labels = Get-Content -Raw -Path $script:LabelsPath | ConvertFrom-Json

    It 'is a non-empty array' {
        $script:Labels.Count | Should BeGreaterThan 0
    }

    It 'every entry has a non-empty name, color and description' {
        foreach ($l in $script:Labels) {
            [string]::IsNullOrEmpty($l.name) | Should Be $false
            [string]::IsNullOrEmpty($l.color) | Should Be $false
            [string]::IsNullOrEmpty($l.description) | Should Be $false
        }
    }

    It 'has unique names, case-insensitive' {
        $lower = $script:Labels | ForEach-Object { $_.name.ToLowerInvariant() }
        ($lower | Select-Object -Unique).Count | Should Be $lower.Count
    }

    It 'every color is a bare 6-digit lowercase hex string' {
        foreach ($l in $script:Labels) {
            $l.color | Should Match '^[0-9a-f]{6}$'
        }
    }

    It 'every name matches the "<prefix>: value" convention, a priority label, or a known community label' {
        foreach ($l in $script:Labels) {
            $name = $l.name
            $isPrefixed = $name -match $script:NamePattern
            $isPriority = $name -match $script:PriorityPattern
            $isCommunity = ($name -eq 'good first issue') -or ($name -eq 'help wanted')
            ($isPrefixed -or $isPriority -or $isCommunity) | Should Be $true
        }
    }

    It 'every description is 100 characters or fewer' {
        foreach ($l in $script:Labels) {
            ($l.description.Length -le 100) | Should Be $true
        }
    }

    It 'renameFrom values are unique' {
        $renames = @($script:Labels | Where-Object { $_.renameFrom } | ForEach-Object { $_.renameFrom })
        ($renames | Select-Object -Unique).Count | Should Be $renames.Count
    }

    It 'renameFrom values are only the known GitHub stock label names' {
        $renames = @($script:Labels | Where-Object { $_.renameFrom } | ForEach-Object { $_.renameFrom })
        foreach ($r in $renames) {
            ($script:StockRenameFroms -contains $r) | Should Be $true
        }
    }

    It 'every stock label to rename appears exactly once as a renameFrom' {
        $renames = @($script:Labels | Where-Object { $_.renameFrom } | ForEach-Object { $_.renameFrom })
        foreach ($stock in $script:StockRenameFroms) {
            ($renames | Where-Object { $_ -eq $stock }).Count | Should Be 1
        }
    }
}

Describe 'sync-labels.ps1' {

    It 'exists and parses as valid PowerShell' {
        Test-Path $script:SyncScriptPath | Should Be $true
        $errors = $null
        [System.Management.Automation.Language.Parser]::ParseFile($script:SyncScriptPath, [ref]$null, [ref]$errors) | Out-Null
        $errors.Count | Should Be 0
    }
}
