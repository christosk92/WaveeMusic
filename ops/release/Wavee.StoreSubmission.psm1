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
        # a new file named like one the listing already holds would make the delete and the upload ambiguous
        if (@($images | Where-Object { $_.FileName -ieq $shot.FileName }).Count -gt 0) { throw "'$($shot.FileName)' is already in the listing; give the new set its own file names (the store kit suffixes the version)." }
        $images += [pscustomobject]@{ FileName = $shot.FileName; FileStatus = 'PendingUpload'; Description = $shot.Caption; ImageType = 'Screenshot' }
    }
    $base | Add-Member -NotePropertyName Images -NotePropertyValue $images -Force
    return $copy
}

function Assert-WaveeStoreScreenshots {
    # The round trip: the listing's active screenshots are exactly the new set, in order. msstore-cli's image model has
    # no Description, so captions never round-trip through it: they are sent, but only file names can be asserted.
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
        if ($active[$i].FileName -cne $Shots[$i].FileName) {
            throw "Screenshot $($i + 1) is '$($active[$i].FileName)', expected '$($Shots[$i].FileName)'."
        }
    }
}

function Get-WaveeStorePngSize {
    # Width and height from a PNG's IHDR chunk (bytes 16..23, big-endian), without loading the image.
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Path)
    $fs = [IO.File]::OpenRead($Path)
    try {
        $b = New-Object byte[] 24
        if ($fs.Read($b, 0, 24) -ne 24 -or $b[0] -ne 0x89 -or $b[1] -ne 0x50 -or $b[2] -ne 0x4E -or $b[3] -ne 0x47) { throw "Not a PNG: '$Path'." }
        $w = ([int]$b[16] -shl 24) -bor ([int]$b[17] -shl 16) -bor ([int]$b[18] -shl 8) -bor [int]$b[19]
        $h = ([int]$b[20] -shl 24) -bor ([int]$b[21] -shl 16) -bor ([int]$b[22] -shl 8) -bor [int]$b[23]
        return [pscustomobject]@{ Width = $w; Height = $h }
    }
    finally { $fs.Dispose() }
}

function Get-WaveeStoreListingMedia {
    # The listing's trailer (video + 1920x1080 PNG thumbnail + title) and 16:9 super hero art, checked against
    # Partner Center's rules before anything is uploaded. Either part may be absent. A trailer only shows at the top
    # of the listing when a 16:9 super hero art is there too (this run's or the one already published).
    [CmdletBinding()]
    param([string]$TrailerFile = '', [string]$TrailerThumbnail = '', [string]$TrailerTitle = '', [string]$SuperHeroFile = '')
    $plain = { param($p) if ((Split-Path -Leaf $p) -notmatch '^[A-Za-z0-9._-]+$') { throw "Listing file names must be plain ASCII: '$p'." } }
    $trailer = $null; $hero = $null
    if ($TrailerFile) {
        if (-not (Test-Path -LiteralPath $TrailerFile -PathType Leaf)) { throw "Trailer not found: '$TrailerFile'." }
        if ([IO.Path]::GetExtension($TrailerFile) -notin @('.mp4', '.mov')) { throw "A trailer is an .mp4 or .mov: '$TrailerFile'." }
        if ((Get-Item -LiteralPath $TrailerFile).Length -ge 2GB) { throw "Trailers must be under 2 GB: '$TrailerFile'." }
        if (-not $TrailerThumbnail) { throw 'A trailer needs its thumbnail (-TrailerThumbnail, a 1920x1080 PNG).' }
        if (-not (Test-Path -LiteralPath $TrailerThumbnail -PathType Leaf)) { throw "Trailer thumbnail not found: '$TrailerThumbnail'." }
        $ts = Get-WaveeStorePngSize -Path $TrailerThumbnail
        if ($ts.Width -ne 1920 -or $ts.Height -ne 1080) { throw "The trailer thumbnail must be 1920x1080, not $($ts.Width)x$($ts.Height): '$TrailerThumbnail'." }
        $title = "$TrailerTitle".Trim()
        if ($title.Length -eq 0 -or $title.Length -gt 255) { throw 'A trailer title is 1 to 255 characters.' }
        & $plain $TrailerFile; & $plain $TrailerThumbnail
        $trailer = [pscustomobject]@{
            FileName = (Split-Path -Leaf $TrailerFile); Path = (Resolve-Path -LiteralPath $TrailerFile).Path; Title = $title
            Thumb = [pscustomobject]@{ FileName = (Split-Path -Leaf $TrailerThumbnail); Path = (Resolve-Path -LiteralPath $TrailerThumbnail).Path }
        }
    }
    if ($SuperHeroFile) {
        if (-not (Test-Path -LiteralPath $SuperHeroFile -PathType Leaf)) { throw "Super hero art not found: '$SuperHeroFile'." }
        $hs = Get-WaveeStorePngSize -Path $SuperHeroFile
        if (-not (($hs.Width -eq 1920 -and $hs.Height -eq 1080) -or ($hs.Width -eq 3840 -and $hs.Height -eq 2160))) {
            throw "16:9 super hero art is 1920x1080 or 3840x2160, not $($hs.Width)x$($hs.Height): '$SuperHeroFile'."
        }
        if ((Get-Item -LiteralPath $SuperHeroFile).Length -ge 50MB) { throw "Listing images must be under 50 MB: '$SuperHeroFile'." }
        & $plain $SuperHeroFile
        $hero = [pscustomobject]@{ FileName = (Split-Path -Leaf $SuperHeroFile); Path = (Resolve-Path -LiteralPath $SuperHeroFile).Path }
    }
    $files = @()
    if ($trailer) { $files += [pscustomobject]@{ FileName = $trailer.FileName; Path = $trailer.Path }; $files += $trailer.Thumb }
    if ($hero) { $files += $hero }
    return [pscustomobject]@{ Trailer = $trailer; SuperHero = $hero; Files = $files }
}

function Set-WaveeStoreListingMedia {
    # Replace the trailer and/or the 16:9 super hero art. The submission API removes a trailer that is missing from the
    # Trailers array, so the new one replaces whatever was there; the old super hero image is marked PendingDelete
    # and the new one added as PendingUpload. Files travel in the submission's upload zip. Returns a deep copy.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Submission,
        [Parameter(Mandatory = $true)]$Media,
        [string]$Language = 'en-us')
    $copy = $Submission | ConvertTo-Json -Depth 100 | ConvertFrom-Json
    $listingProp = @($copy.Listings.PSObject.Properties | Where-Object { $_.Name -ieq $Language })
    if ($listingProp.Count -ne 1) { throw "Submission has no '$Language' listing." }
    $base = $listingProp[0].Value.BaseListing
    if ($null -eq $base) { throw "The '$Language' listing has no BaseListing." }
    if ($Media.SuperHero) {
        $images = @()
        foreach ($image in @($base.Images)) {
            if ($null -eq $image) { continue }
            if ($image.ImageType -eq 'PromotionalArt16x9') { $image.FileStatus = 'PendingDelete' }
            $images += $image
        }
        if (@($images | Where-Object { $_.FileName -ieq $Media.SuperHero.FileName }).Count -gt 0) { throw "'$($Media.SuperHero.FileName)' is already in the listing; give the new super hero art its own file name." }
        $images += [pscustomobject]@{ FileName = $Media.SuperHero.FileName; FileStatus = 'PendingUpload'; ImageType = 'PromotionalArt16x9' }
        $base | Add-Member -NotePropertyName Images -NotePropertyValue $images -Force
    }
    if ($Media.Trailer) {
        $assets = [pscustomobject]@{}
        $assets | Add-Member -NotePropertyName $Language -NotePropertyValue ([pscustomobject]@{
            Title = $Media.Trailer.Title
            ImageList = @([pscustomobject]@{ FileName = $Media.Trailer.Thumb.FileName; Description = $Media.Trailer.Title })
        })
        $trailers = @([pscustomobject]@{ VideoFileName = $Media.Trailer.FileName; TrailerAssets = $assets })
        $copy | Add-Member -NotePropertyName Trailers -NotePropertyValue $trailers -Force
        $heroes = @($base.Images | Where-Object { $_ -and $_.ImageType -eq 'PromotionalArt16x9' -and $_.FileStatus -ne 'PendingDelete' })
        if ($heroes.Count -eq 0) { Write-Warning 'No 16:9 super hero art in the listing: the trailer will not show at the top of the Store page.' }
    }
    return $copy
}

function Assert-WaveeStoreListingMedia {
    # The round trip on the Store's own copy after the update: exactly the new trailer (video, title, thumbnail) and
    # one active super hero image, the new file.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Submission,
        [Parameter(Mandatory = $true)]$Media,
        [string]$Language = 'en-us')
    if ($Media.Trailer) {
        $t = @($Submission.Trailers | Where-Object { $_ })
        if ($t.Count -ne 1) { throw "The submission holds $($t.Count) trailers, expected 1." }
        if ($t[0].VideoFileName -cne $Media.Trailer.FileName) { throw "The trailer is '$($t[0].VideoFileName)', expected '$($Media.Trailer.FileName)'." }
        $assetProp = @($t[0].TrailerAssets.PSObject.Properties | Where-Object { $_.Name -ieq $Language })
        if ($assetProp.Count -ne 1) { throw "The trailer has no '$Language' assets." }
        $a = $assetProp[0].Value
        if ($a.Title -cne $Media.Trailer.Title) { throw "The trailer title is '$($a.Title)', expected '$($Media.Trailer.Title)'." }
        $thumbs = @($a.ImageList | Where-Object { $_ })
        if ($thumbs.Count -ne 1 -or $thumbs[0].FileName -cne $Media.Trailer.Thumb.FileName) { throw 'The trailer thumbnail did not round-trip.' }
    }
    if ($Media.SuperHero) {
        $listingProp = @($Submission.Listings.PSObject.Properties | Where-Object { $_.Name -ieq $Language })
        if ($listingProp.Count -ne 1) { throw "Submission has no '$Language' listing." }
        $active = @($listingProp[0].Value.BaseListing.Images | Where-Object { $_ -and $_.ImageType -eq 'PromotionalArt16x9' -and $_.FileStatus -ne 'PendingDelete' })
        if ($active.Count -ne 1 -or $active[0].FileName -cne $Media.SuperHero.FileName) {
            throw "The listing's 16:9 super hero art is '$(@($active | ForEach-Object FileName) -join ', ')', expected '$($Media.SuperHero.FileName)'."
        }
    }
}

function Resolve-WaveeStoreListingDir {
    # One folder holds a whole listing refresh (the store kit's deliver folder):
    #   screenshots\NN-name.png + NN-name.caption.txt      trailer\<name>.mp4 + <name>-thumb.png [+ title.txt]
    #   super-hero\super-hero-3840x2160.png (or the only *.png there)
    # Returns the paths; a missing part is '' (that part of the listing is left as it is).
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Dir, [Parameter(Mandatory = $true)][string]$DefaultTitle)
    if (-not (Test-Path -LiteralPath $Dir -PathType Container)) { throw "Listing folder not found: '$Dir'." }
    $one = { param($sub, $filter, $what)
        $d = Join-Path $Dir $sub
        if (-not (Test-Path -LiteralPath $d -PathType Container)) { return '' }
        $hits = @(Get-ChildItem -LiteralPath $d -Filter $filter -File | Sort-Object Name)
        if ($hits.Count -gt 1) { throw "More than one $what in '$d': $(($hits | ForEach-Object Name) -join ', ')." }
        if ($hits.Count -eq 1) { return $hits[0].FullName } else { return '' } }
    $shots = Join-Path $Dir 'screenshots'
    $video = & $one 'trailer' '*.mp4' 'trailer video'
    $thumb = & $one 'trailer' '*-thumb.png' 'trailer thumbnail'
    $titleFile = Join-Path $Dir 'trailer\title.txt'
    $title = if (Test-Path -LiteralPath $titleFile) { [IO.File]::ReadAllText($titleFile).TrimStart([char]0xFEFF).Trim() } else { $DefaultTitle }
    $heroDir = Join-Path $Dir 'super-hero'
    $hero = if (Test-Path -LiteralPath (Join-Path $heroDir 'super-hero-3840x2160.png')) { Join-Path $heroDir 'super-hero-3840x2160.png' } else { & $one 'super-hero' '*.png' 'super hero image' }
    return [pscustomobject]@{
        ScreenshotDir = $(if (Test-Path -LiteralPath $shots -PathType Container) { $shots } else { '' })
        TrailerFile = $video; TrailerThumbnail = $thumb; TrailerTitle = $title; SuperHeroFile = $hero
    }
}

Export-ModuleMember -Function Set-WaveeStorePackageSet, Assert-WaveeStorePackageSet, Assert-WaveeStoreArtifactHashes,
    Get-WaveeStoreShotSet, Set-WaveeStoreScreenshots, Assert-WaveeStoreScreenshots,
    Get-WaveeStorePngSize, Get-WaveeStoreListingMedia, Set-WaveeStoreListingMedia, Assert-WaveeStoreListingMedia,
    Resolve-WaveeStoreListingDir
