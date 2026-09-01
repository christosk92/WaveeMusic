#Requires -Version 5.1
<#
    Syncs the repo's GitHub labels to match ops/github/labels.json, which is the single source of
    truth. There is no CI in this repo (see CLAUDE.md), so this is run locally with the `gh` CLI
    whenever labels.json changes.

    Idempotent: re-running it with an unchanged labels.json is a no-op (every label already matches,
    so each `gh label create --force` just re-applies the same color/description). It never deletes a
    label - anything on the repo that isn't in labels.json is reported at the end and left alone.

    The seven `renameFrom` entries exist so the stock GitHub labels (bug, enhancement, documentation,
    question, duplicate, wontfix, invalid) are renamed in place rather than recreated, which preserves
    their existing issue/PR assignments.

    Usage:
        powershell -File ops/github/sync-labels.ps1                # apply
        powershell -File ops/github/sync-labels.ps1 -DryRun         # print the plan only
        powershell -File ops/github/sync-labels.ps1 -Repo owner/name -LabelsPath path\to\labels.json
#>

param(
    [string]$Repo = 'christosk92/WaveeMusic',
    [switch]$DryRun,
    [string]$LabelsPath = (Join-Path $PSScriptRoot 'labels.json')
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "The GitHub CLI ('gh') was not found on PATH - install it from https://cli.github.com and sign in before running this script."
}

$labels = Get-Content -Raw -Path $LabelsPath | ConvertFrom-Json

Write-Host "Fetching existing labels for $Repo ..."
$existingJson = gh label list --repo $Repo --limit 200 --json name,color,description
if ($LASTEXITCODE -ne 0) { throw "gh label list failed for $Repo (exit $LASTEXITCODE)" }
$existing = $existingJson | ConvertFrom-Json
$existingNames = @{}
foreach ($e in $existing) { $existingNames[$e.name] = $true }

$created = 0
$updated = 0
$renamed = 0

foreach ($label in $labels) {
    $name = $label.name
    $color = $label.color
    $desc = $label.description
    $renameFrom = $label.renameFrom

    if ($renameFrom -and $existingNames.ContainsKey($renameFrom) -and -not $existingNames.ContainsKey($name)) {
        if ($DryRun) {
            Write-Host "[dry-run] rename  $renameFrom -> $name"
        } else {
            Write-Host "rename  $renameFrom -> $name"
            gh label edit $renameFrom --repo $Repo --name $name --color $color --description $desc
            if ($LASTEXITCODE -ne 0) { throw "gh label edit failed for '$name' (renamed from '$renameFrom', exit $LASTEXITCODE)" }
        }
        $existingNames.Remove($renameFrom) | Out-Null
        $existingNames[$name] = $true
        $renamed++
        continue
    }

    $verb = if ($existingNames.ContainsKey($name)) { 'update' } else { 'create' }
    if ($DryRun) {
        Write-Host "[dry-run] $verb  $name"
    } else {
        Write-Host "$verb  $name"
        gh label create $name --repo $Repo --color $color --description $desc --force
        if ($LASTEXITCODE -ne 0) { throw "gh label create failed for '$name' (exit $LASTEXITCODE)" }
    }
    $existingNames[$name] = $true
    if ($verb -eq 'create') { $created++ } else { $updated++ }
}

$labelNames = @{}
foreach ($l in $labels) { $labelNames[$l.name] = $true }
# $existingNames already reflects the renames above, so a renamed stock label is not reported as extra.
$extra = @($existingNames.Keys | Where-Object { -not $labelNames.ContainsKey($_) } | Sort-Object)

Write-Host ''
Write-Host "Summary: created=$created updated=$updated renamed=$renamed"
foreach ($name in $extra) {
    Write-Host "extra (left alone): $name"
}
