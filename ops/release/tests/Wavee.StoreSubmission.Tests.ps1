#requires -Version 5.1
Import-Module (Join-Path $PSScriptRoot '..\Wavee.StoreSubmission.psm1') -Force -DisableNameChecking

function New-PackageSetFixture {
    [pscustomobject]@{
        Id = 'submission-809'; TargetPublishMode = 'Manual'
        Listings = [pscustomobject]@{ 'en-us' = [pscustomobject]@{ Description = 'preserved'; ReleaseNotes = 'notes' } }
        ApplicationPackages = @(
            [pscustomobject]@{ Id = 'old-arm'; FileName = 'Wavee_1.2.102.0_arm64.msix'; Version = '1.2.102.0'; Architecture = 'Arm64'; FileStatus = 'Uploaded' }
            [pscustomobject]@{ Id = 'old-x64'; FileName = 'Wavee_1.2.102.0_x64.msix'; Version = '1.2.102.0'; Architecture = 'X64'; FileStatus = 'Uploaded' }
            [pscustomobject]@{ Id = 'bad-809'; FileName = 'Wavee_1.2.809.0_store.msixupload'; Version = '1.2.809.0'; Architecture = 'X64'; FileStatus = 'PendingDelete' }
            [pscustomobject]@{ Id = ''; FileName = 'Wavee_1.2.809.0_bundle.msixupload'; Version = ''; Architecture = ''; FileStatus = 'PendingUpload' }
        )
    }
}
$script:PackageSetArgs = @{ SubmissionId = 'submission-809'; UploadName = 'Wavee_1.2.809.0_bundle.msixupload'; Quad = '1.2.809.0' }

Describe 'Store package-set reconciliation' {
    It 'retires both 102 packages missed by extension-only CLI replacement and preserves metadata' {
        $before = New-PackageSetFixture
        $after = Set-WaveeStorePackageSet -Submission $before @script:PackageSetArgs
        @($after.ApplicationPackages | Where-Object FileStatus -eq PendingDelete).Count | Should Be 3
        $after.TargetPublishMode | Should Be Immediate
        $after.Listings.'en-us'.Description | Should Be preserved
        $before.ApplicationPackages[0].FileStatus | Should Be Uploaded
        $before.TargetPublishMode | Should Be Manual
    }
    It 'refuses an unrelated submission ID' {
        $s = New-PackageSetFixture; $s.Id = 'other'
        { Set-WaveeStorePackageSet -Submission $s @script:PackageSetArgs } | Should Throw
    }
    It 'refuses a newer old package' {
        $s = New-PackageSetFixture; $s.ApplicationPackages[0].Version = '1.2.810.0'
        { Set-WaveeStorePackageSet -Submission $s @script:PackageSetArgs } | Should Throw
    }
    It 'refuses unknown architecture even when CLI already marked it deleted' {
        $s = New-PackageSetFixture; $s.ApplicationPackages[2].Architecture = 'x86'
        { Set-WaveeStorePackageSet -Submission $s @script:PackageSetArgs } | Should Throw
    }
    It 'refuses another pending upload' {
        $s = New-PackageSetFixture; $s.ApplicationPackages[0].FileStatus = 'PendingUpload'
        { Set-WaveeStorePackageSet -Submission $s @script:PackageSetArgs } | Should Throw
    }
    It 'refuses missing or duplicate old package IDs' {
        $s = New-PackageSetFixture; $s.ApplicationPackages[0].Id = ''
        { Set-WaveeStorePackageSet -Submission $s @script:PackageSetArgs } | Should Throw
        $s = New-PackageSetFixture; $s.ApplicationPackages[0].Id = 'old-x64'
        { Set-WaveeStorePackageSet -Submission $s @script:PackageSetArgs } | Should Throw
    }
    It 'refuses duplicate or absent expected upload' {
        $s = New-PackageSetFixture; $s.ApplicationPackages += $s.ApplicationPackages[3]
        { Set-WaveeStorePackageSet -Submission $s @script:PackageSetArgs } | Should Throw
        $s = New-PackageSetFixture; $s.ApplicationPackages = @($s.ApplicationPackages[0..2])
        { Set-WaveeStorePackageSet -Submission $s @script:PackageSetArgs } | Should Throw
    }
    It 'allows retiring an older same-name upload without confusing it for the new upload' {
        $s = New-PackageSetFixture; $s.ApplicationPackages[2].FileName = $script:PackageSetArgs.UploadName
        $after = Set-WaveeStorePackageSet -Submission $s @script:PackageSetArgs
        @($after.ApplicationPackages | Where-Object FileStatus -ne PendingDelete).Count | Should Be 1
    }
}

Describe 'Store draft and ingestion assertions' {
    It 'accepts a complete draft but rejects active 102 leftovers and unknown file statuses' {
        $s = Set-WaveeStorePackageSet -Submission (New-PackageSetFixture) @script:PackageSetArgs
        { Assert-WaveeStorePackageSet -Submission $s @script:PackageSetArgs } | Should Not Throw
        $s.ApplicationPackages[0].FileStatus = 'Uploaded'
        { Assert-WaveeStorePackageSet -Submission $s @script:PackageSetArgs } | Should Throw
        $s.ApplicationPackages[0].FileStatus = 'Mystery'
        { Assert-WaveeStorePackageSet -Submission $s @script:PackageSetArgs } | Should Throw
    }
    It 'rejects raw x64-only and arm64-only ingested uploads' {
        foreach ($arch in @('X64', 'Arm64')) {
            $s = Set-WaveeStorePackageSet -Submission (New-PackageSetFixture) @script:PackageSetArgs
            $s.ApplicationPackages = @($s.ApplicationPackages[3])
            $p = $s.ApplicationPackages[0]; $p.Id = 'new'; $p.Architecture = $arch; $p.Version = '1.2.809.0'; $p.FileStatus = 'Uploaded'
            { Assert-WaveeStorePackageSet -Submission $s @script:PackageSetArgs -Ingested } | Should Throw
        }
    }
    It 'accepts the Neutral ingested bundle and rejects wrong versions or incomplete ingestion' {
        $s = Set-WaveeStorePackageSet -Submission (New-PackageSetFixture) @script:PackageSetArgs
        $s.ApplicationPackages = @($s.ApplicationPackages[3])
        $p = $s.ApplicationPackages[0]; $p.Id = 'new'; $p.Architecture = 'Neutral'; $p.Version = '1.2.809.0'; $p.FileStatus = 'Uploaded'
        { Assert-WaveeStorePackageSet -Submission $s @script:PackageSetArgs -Ingested } | Should Not Throw
        $p.Version = '1.2.102.0'
        { Assert-WaveeStorePackageSet -Submission $s @script:PackageSetArgs -Ingested } | Should Throw
        $p.Version = '1.2.809.0'; $p.FileStatus = 'PendingUpload'
        { Assert-WaveeStorePackageSet -Submission $s @script:PackageSetArgs -Ingested } | Should Throw
    }
    It 'requires automatic publishing as selected by the user' {
        $s = Set-WaveeStorePackageSet -Submission (New-PackageSetFixture) @script:PackageSetArgs
        $s.TargetPublishMode = 'Manual'
        { Assert-WaveeStorePackageSet -Submission $s @script:PackageSetArgs } | Should Throw
    }
}

Describe 'Store artifact hash ledger' {
    It 'validates unchanged files and detects tampering, absence, duplicates and malformed hashes' {
        $path = Join-Path $TestDrive 'artifact.msix'
        [IO.File]::WriteAllText($path, 'original')
        $entry = [pscustomobject]@{ Path = $path; Sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
        { Assert-WaveeStoreArtifactHashes -Artifacts @($entry) } | Should Not Throw
        { Assert-WaveeStoreArtifactHashes -Artifacts @($entry, $entry) } | Should Throw
        [IO.File]::WriteAllText($path, 'changed')
        { Assert-WaveeStoreArtifactHashes -Artifacts @($entry) } | Should Throw
        $entry.Path = Join-Path $TestDrive 'absent.msix'
        { Assert-WaveeStoreArtifactHashes -Artifacts @($entry) } | Should Throw
        $entry.Path = $path; $entry.Sha256 = 'not-a-hash'
        { Assert-WaveeStoreArtifactHashes -Artifacts @($entry) } | Should Throw
    }
}

Describe 'Store command entrypoint refuses bypass switches before any release work' {
    It 'rejects Force with the explicit non-bypassable gate error' {
        $command = Join-Path $PSScriptRoot '..\wavee-store-submit.ps1'
        $failure = $null
        try { & $command -Force } catch { $failure = $_ }
        ($null -ne $failure) | Should Be $true
        $failure.Exception.Message | Should Match 'Force is not supported for Store submissions'
    }
    It 'rejects the removed single-architecture argument during parameter binding' {
        $command = Join-Path $PSScriptRoot '..\wavee-store-submit.ps1'
        $failure = $null
        try { & $command -Arch x64 } catch { $failure = $_ }
        ($null -ne $failure) | Should Be $true
        $failure.FullyQualifiedErrorId | Should Match 'NamedParameterNotFound'
    }
}

function Write-ResumeFixture {
    param([string]$OutputRoot, [hashtable]$Ledger)
    $repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
    [xml]$props = [IO.File]::ReadAllText((Join-Path $repo 'src\apps\Wavee\Wavee.Version.props'))
    $version = [string]$props.Project.PropertyGroup.WaveeVersion
    $directory = Join-Path $OutputRoot $version
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    [IO.File]::WriteAllText((Join-Path $directory 'store-state.json'), ($Ledger | ConvertTo-Json -Depth 10))
}

Describe 'Store resume refuses unsafe ledgers before any network activity' {
    It 'refuses an abort ledger belonging to another product before any Store call' {
        $directory = Join-Path $TestDrive 'wrong-abort-product'
        Write-ResumeFixture $directory @{ schema = 2; productId = 'other'; submissionId = ''; committed = $false }
        $product = 'test' + [guid]::NewGuid().ToString('N')
        $failure = $null
        try { & (Join-Path $PSScriptRoot '..\wavee-store-submit.ps1') -Abort -OutputDir $directory -ProductId $product 6>$null } catch { $failure = $_ }
        $failure.Exception.Message | Should Match 'Abort product does not match the ledger'
    }
    It 'preserves an empty artifact array when resuming before the first package' {
        $directory = Join-Path $TestDrive 'empty-artifacts'
        $packages = Join-Path $TestDrive 'empty-inputs'
        New-Item -ItemType Directory -Path $packages | Out-Null
        $repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
        [xml]$props = [IO.File]::ReadAllText((Join-Path $repo 'src\apps\Wavee\Wavee.Version.props'))
        $version = [string]$props.Project.PropertyGroup.WaveeVersion
        $product = 'test' + [guid]::NewGuid().ToString('N')
        Write-ResumeFixture $directory @{
            schema = 2; packageDir = $packages; x64Msix = ''; sourceRoot = $repo
            identityName = 'cproducts.Wavee'; publisher = 'CN=88D90E00-BEC4-41D6-8623-9F49F1AE2E9E'
            productId = $product; semver = $version; codename = 'fixture'; quad = '1.2.809.0'
            tag = "wavee-v$version"; commit = ('0' * 40); artifacts = @(); arches = @('arm64','x64')
            phases = @{ preflight = 'done' }
        }
        $failure = $null
        try { & (Join-Path $PSScriptRoot '..\wavee-store-submit.ps1') -Resume -OutputDir $directory -ProductId $product 6>$null } catch { $failure = $_ }
        # Deliberately wrong commit stops before network, after empty-ledger restoration succeeds.
        $failure.Exception.Message | Should Match 'Release source HEAD changed'
    }
    It 'rejects schema 1 rather than replaying the old single-architecture submission' {
        $directory = Join-Path $TestDrive 'schema-one'
        Write-ResumeFixture $directory @{ schema = 1 }
        $failure = $null
        try { & (Join-Path $PSScriptRoot '..\wavee-store-submit.ps1') -Resume -OutputDir $directory -ProductId ('test' + [guid]::NewGuid().ToString('N')) } catch { $failure = $_ }
        ($null -ne $failure) | Should Be $true
        $failure.Exception.Message | Should Match 'ledger predates bundle verification'
    }
    It 'rejects incomplete schema 2 without recorded adoption inputs' {
        $directory = Join-Path $TestDrive 'missing-mode'
        Write-ResumeFixture $directory @{ schema = 2; phases = @{ preflight = 'done'; packArm64 = 'done' } }
        $failure = $null
        try { & (Join-Path $PSScriptRoot '..\wavee-store-submit.ps1') -Resume -OutputDir $directory -ProductId ('test' + [guid]::NewGuid().ToString('N')) } catch { $failure = $_ }
        ($null -ne $failure) | Should Be $true
        $failure.Exception.Message | Should Match 'Incomplete ledger lacks recorded adoption inputs'
    }
    It 'rejects a different archive directory on resume' {
        $directory = Join-Path $TestDrive 'changed-mode'
        $original = Join-Path $TestDrive 'original-packages'
        $replacement = Join-Path $TestDrive 'replacement-packages'
        New-Item -ItemType Directory -Path $original, $replacement | Out-Null
        Write-ResumeFixture $directory @{ schema = 2; packageDir = $original; x64Msix = '' }
        $failure = $null
        try { & (Join-Path $PSScriptRoot '..\wavee-store-submit.ps1') -Resume -OutputDir $directory -PackageDir $replacement -ProductId ('test' + [guid]::NewGuid().ToString('N')) } catch { $failure = $_ }
        ($null -ne $failure) | Should Be $true
        $failure.Exception.Message | Should Match 'Resume cannot change the archived package directory'
    }
    It 'rejects a concurrent product owner in another PowerShell process before reading a ledger' {
        $product = 'test' + [guid]::NewGuid().ToString('N')
        $mutex = New-Object Threading.Mutex($false, "Global\Wavee.Store.$product")
        $taken = $mutex.WaitOne(0)
        $process = $null
        try {
            $taken | Should Be $true
            $command = (Join-Path $PSScriptRoot '..\wavee-store-submit.ps1').Replace("'", "''")
            $missing = (Join-Path $TestDrive 'no-ledger').Replace("'", "''")
            # Missing-ledger Resume is also fail-closed if the mutex guard regresses: no Store calls are possible.
            $child = "try { & '$command' -Resume -OutputDir '$missing' -ProductId '$product'; exit 0 } catch { [Console]::Out.WriteLine(`$_.Exception.Message); exit 17 }"
            $start = New-Object Diagnostics.ProcessStartInfo
            $start.FileName = 'powershell.exe'
            $start.Arguments = '-NoProfile -NonInteractive -EncodedCommand ' + [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($child))
            $start.UseShellExecute = $false
            $start.CreateNoWindow = $true
            $start.RedirectStandardOutput = $true
            $start.RedirectStandardError = $true
            $process = [Diagnostics.Process]::Start($start)
            if (-not $process.WaitForExit(30000)) { $process.Kill(); throw 'Mutex-test child timed out.' }
            $output = $process.StandardOutput.ReadToEnd()
            $process.ExitCode | Should Be 17
            $output | Should Match 'Another Store release process already owns this product'
        }
        finally {
            if ($process) { $process.Dispose() }
            if ($taken) { $mutex.ReleaseMutex() }
            $mutex.Dispose()
        }
    }
}
