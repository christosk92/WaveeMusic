#requires -Version 5.1
# Pure submission decisions. Callers must validate the local dual-architecture bundle first.

function Assert-StoreSubmissionIdentity {
    param($Submission, [string]$SubmissionId)
    if ([string]::IsNullOrWhiteSpace($SubmissionId) -or [string]$Submission.Id -cne $SubmissionId) {
        throw 'Store submission ID does not match the recorded submission; refusing to touch another draft.'
    }
}

function ConvertTo-StorePackageVersion {
    param([string]$Value)
    if ($Value -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw "Unknown Store package version '$Value'." }
    try { return [version]$Value } catch { throw "Invalid Store package version '$Value'." }
}

function Get-StoreExpectedPackage {
    param($Submission, [string]$UploadName)
    if ([IO.Path]::GetFileName($UploadName) -cne $UploadName -or $UploadName -notmatch '\.msixupload$') {
        throw 'Expected upload must be a plain .msixupload filename.'
    }
    $matches = @($Submission.ApplicationPackages | Where-Object { $_.FileName -ceq $UploadName -and $_.FileStatus -ne 'PendingDelete' })
    if ($matches.Count -ne 1) { throw "Expected exactly one active '$UploadName' upload; found $($matches.Count)." }
    return $matches[0]
}

function Set-WaveeStorePackageSet {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Submission,
        [Parameter(Mandatory = $true)][string]$SubmissionId,
        [Parameter(Mandatory = $true)][string]$UploadName,
        [Parameter(Mandatory = $true)][string]$Quad)

    Assert-StoreSubmissionIdentity $Submission $SubmissionId
    $version = ConvertTo-StorePackageVersion $Quad
    # Deep clone: never mutate the caller's snapshot or discard unrelated listing metadata.
    $copy = $Submission | ConvertTo-Json -Depth 100 | ConvertFrom-Json
    $expected = Get-StoreExpectedPackage $copy $UploadName
    if ($expected.FileStatus -notin @('PendingUpload', 'Uploaded')) { throw 'Expected upload has an unknown file status.' }
    $ids = @{}
    foreach ($package in @($copy.ApplicationPackages)) {
        $id = [string]$package.Id
        if (-not [string]::IsNullOrWhiteSpace($id)) {
            if ($ids.ContainsKey($id)) { throw "Duplicate Store package ID '$id'." }
            $ids[$id] = $true
        }
        if ([object]::ReferenceEquals($package, $expected)) { continue }
        if ([string]::IsNullOrWhiteSpace($id)) { throw 'Existing package lacks an ID; cannot safely retire it.' }
        if ($package.FileStatus -notin @('Uploaded', 'PendingDelete')) { throw "Unexpected old package status '$($package.FileStatus)' for ID $id." }
        if ($package.Architecture -notin @('x64', 'arm64', 'neutral')) { throw "Unknown old package architecture '$($package.Architecture)' for ID $id." }
        if ((ConvertTo-StorePackageVersion ([string]$package.Version)) -gt $version) { throw "Package ID $id is newer than the proposed replacement." }
        $package.FileStatus = 'PendingDelete'
    }
    $copy | Add-Member -NotePropertyName TargetPublishMode -NotePropertyValue Immediate -Force
    Assert-WaveeStorePackageSet -Submission $copy -SubmissionId $SubmissionId -UploadName $UploadName -Quad $Quad
    return $copy
}

function Assert-WaveeStorePackageSet {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Submission,
        [Parameter(Mandatory = $true)][string]$SubmissionId,
        [Parameter(Mandatory = $true)][string]$UploadName,
        [Parameter(Mandatory = $true)][string]$Quad,
        [switch]$Ingested)

    Assert-StoreSubmissionIdentity $Submission $SubmissionId
    $version = ConvertTo-StorePackageVersion $Quad
    if ($Submission.TargetPublishMode -ne 'Immediate') { throw 'Automatic publication must remain Immediate.' }
    $expected = Get-StoreExpectedPackage $Submission $UploadName
    if ($expected.FileStatus -notin @('PendingUpload', 'Uploaded')) { throw 'Expected upload has an unknown file status.' }
    if ($Ingested) {
        if ($expected.FileStatus -ne 'Uploaded' -or $expected.Architecture -ne 'Neutral' -or
            (ConvertTo-StorePackageVersion ([string]$expected.Version)) -ne $version) {
            throw 'Store has not ingested the expected Neutral bundle at the required version.'
        }
        if ([string]::IsNullOrWhiteSpace([string]$expected.Id)) { throw 'Ingested bundle is missing its Store package ID.' }
    }
    $ids = @{}
    foreach ($package in @($Submission.ApplicationPackages)) {
        $id = [string]$package.Id
        if (-not [string]::IsNullOrWhiteSpace($id)) {
            if ($ids.ContainsKey($id)) { throw "Duplicate Store package ID '$id'." }
            $ids[$id] = $true
        }
        if ([object]::ReferenceEquals($package, $expected)) { continue }
        if ($package.FileStatus -ne 'PendingDelete') { throw "Unexpected active package '$($package.FileName)' remains in the submission." }
        if ([string]::IsNullOrWhiteSpace($id) -or $package.Architecture -notin @('x64', 'arm64', 'neutral')) {
            throw 'Retired package must have a known ID and supported architecture.'
        }
        if ((ConvertTo-StorePackageVersion ([string]$package.Version)) -gt $version) { throw 'Refusing retirement of a newer package.' }
    }
}

function Assert-WaveeStoreArtifactHashes {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][object[]]$Artifacts)
    if ($Artifacts.Count -eq 0) { throw 'Artifact ledger is empty.' }
    $paths = @{}
    foreach ($artifact in $Artifacts) {
        $path = [string]$artifact.Path
        if ([string]::IsNullOrWhiteSpace($path) -or -not [IO.Path]::IsPathRooted($path)) { throw 'Ledger artifact paths must be absolute.' }
        $path = [IO.Path]::GetFullPath($path)
        if ($paths.ContainsKey($path)) { throw "Duplicate ledger artifact '$path'." }
        $paths[$path] = $true
        if ([string]$artifact.Sha256 -notmatch '^[a-fA-F0-9]{64}$') { throw "Invalid ledger SHA256 for '$path'." }
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Ledger artifact missing: '$path'." }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne [string]$artifact.Sha256) { throw "Ledger artifact hash mismatch: '$path'." }
    }
}

function Get-WaveeStoreShotSet {
    # The listing screenshots in a folder: every NN-name.png with its NN-name.caption.txt beside it (the set
    # ops/release/tools/New-StoreSet.py writes), in file-name order. A PNG without a caption is not part of the set.
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Dir)
    if (-not (Test-Path -LiteralPath $Dir -PathType Container)) { throw "Screenshot folder not found: '$Dir'." }
    $shots = @()
    foreach ($png in @(Get-ChildItem -LiteralPath $Dir -Filter '*.png' -File | Sort-Object Name)) {
        $captionPath = Join-Path $Dir ([IO.Path]::GetFileNameWithoutExtension($png.Name) + '.caption.txt')
        if (-not (Test-Path -LiteralPath $captionPath)) { continue }
        $caption = [IO.File]::ReadAllText($captionPath).TrimStart([char]0xFEFF).Trim()
        if ($caption.Length -eq 0) { throw "Empty caption: '$captionPath'." }
        if ($caption.Length -gt 200) { throw "Caption is $($caption.Length) characters; Partner Center caps it at 200: '$captionPath'." }
        if ($png.Name -notmatch '^[A-Za-z0-9._-]+$') { throw "Screenshot names must be plain ASCII: '$($png.Name)'." }
        $shots += [pscustomobject]@{ FileName = $png.Name; Path = $png.FullName; Caption = $caption }
    }
    if ($shots.Count -eq 0) { throw "No captioned screenshots in '$Dir'." }
    if ($shots.Count -gt 10) { throw "$($shots.Count) screenshots; a listing shows at most 10 per device family." }
    return $shots
}

function Set-WaveeStoreScreenshots {
    # Replace a listing's screenshots: every existing Screenshot image is marked PendingDelete, the new ones are added
    # as PendingUpload with their captions (the PNGs travel in the submission's upload zip). Other image types (logos,
    # posters, trailers' thumbnails) are left untouched. Returns a deep copy; the caller's snapshot is never mutated.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Submission,
        [Parameter(Mandatory = $true)][object[]]$Shots,
        [string]$Language = 'en-us')
    $copy = $Submission | ConvertTo-Json -Depth 100 | ConvertFrom-Json
    $listingProp = @($copy.Listings.PSObject.Properties | Where-Object { $_.Name -ieq $Language })
    if ($listingProp.Count -ne 1) { throw "Submission has no '$Language' listing." }
    $base = $listingProp[0].Value.BaseListing
    if ($null -eq $base) { throw "The '$Language' listing has no BaseListing." }
    $images = @()
    foreach ($image in @($base.Images)) {
        if ($null -eq $image) { continue }
        if ($image.ImageType -eq 'Screenshot') { $image.FileStatus = 'PendingDelete' }
        $images += $image
    }
    foreach ($shot in $Shots) {
        $images += [pscustomobject]@{ FileName = $shot.FileName; FileStatus = 'PendingUpload'; Description = $shot.Caption; ImageType = 'Screenshot' }
    }
    $base | Add-Member -NotePropertyName Images -NotePropertyValue $images -Force
    return $copy
}

function Assert-WaveeStoreScreenshots {
    # The round trip: the listing's active screenshots are exactly the new set, in order, with their captions.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Submission,
        [Parameter(Mandatory = $true)][object[]]$Shots,
        [string]$Language = 'en-us')
    $listingProp = @($Submission.Listings.PSObject.Properties | Where-Object { $_.Name -ieq $Language })
    if ($listingProp.Count -ne 1) { throw "Submission has no '$Language' listing." }
    $active = @($listingProp[0].Value.BaseListing.Images | Where-Object { $_.ImageType -eq 'Screenshot' -and $_.FileStatus -ne 'PendingDelete' })
    if ($active.Count -ne $Shots.Count) { throw "The listing holds $($active.Count) active screenshots, expected $($Shots.Count)." }
    for ($i = 0; $i -lt $Shots.Count; $i++) {
        if ($active[$i].FileName -cne $Shots[$i].FileName -or $active[$i].Description -cne $Shots[$i].Caption) {
            throw "Screenshot $($i + 1) is '$($active[$i].FileName)', expected '$($Shots[$i].FileName)' with its caption."
        }
    }
}

Export-ModuleMember -Function Set-WaveeStorePackageSet, Assert-WaveeStorePackageSet, Assert-WaveeStoreArtifactHashes,
    Get-WaveeStoreShotSet, Set-WaveeStoreScreenshots, Assert-WaveeStoreScreenshots
