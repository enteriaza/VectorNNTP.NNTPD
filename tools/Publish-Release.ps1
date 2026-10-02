#Requires -Version 5.1
<#
.SYNOPSIS
    Publishes the four VectorNNTP production applications as self-contained x64 Native AOT binaries.

.DESCRIPTION
    Resolves the repository from this script's location. Publishes VectorNNTP.NNTPD,
    VectorNNTP.StorageServer, VectorNNTP.BackFiller, and NNTPCancelMessage into one
    platform directory: release/win-64 or release/linux-64. Identical shared files
    such as RabbitMq.json are copied once. A conflicting file fails the script.

.PARAMETER Platform
    win-64, linux-64, or all.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Platform
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$Solution = Join-Path $RepoRoot 'VectorNNTP.NNTPD.sln'

$Applications = @(
    @{
        Name    = 'VectorNNTP.NNTPD'
        Project = Join-Path $RepoRoot 'src\VectorNNTP.NNTPD\VectorNNTP.NNTPD.csproj'
        Exe     = 'VectorNNTP.NNTPD'
    },
    @{
        Name    = 'VectorNNTP.StorageServer'
        Project = Join-Path $RepoRoot 'src\VectorNNTP.StorageServer\VectorNNTP.StorageServer.csproj'
        Exe     = 'VectorNNTP.StorageServer'
    },
    @{
        Name    = 'VectorNNTP.BackFiller'
        Project = Join-Path $RepoRoot 'src\VectorNNTP.BackFiller\VectorNNTP.BackFiller.csproj'
        Exe     = 'VectorNNTP.BackFiller'
    },
    @{
        Name    = 'NNTPCancelMessage'
        Project = Join-Path $RepoRoot 'src\VectorNNTP.NNTPCancelMessage\VectorNNTP.NNTPCancelMessage.csproj'
        Exe     = 'NNTPCancelMessage'
    }
)

$RequiredJson = @(
    'VectorNNTP.NNTPD.json',
    'VectorNNTP.StorageServer.json',
    'VectorNNTP.BackFiller.json',
    'NNTPCancelMessage.json',
    'RabbitMq.json'
)

function Fail {
    param([string] $Message)
    throw $Message
}

function Get-PlatformMap {
    param([string] $Name)
    switch ($Name) {
        'win-64' { return @{ Folder = 'win-64'; Rid = 'win-x64'; ExeSuffix = '.exe' } }
        'linux-64' { return @{ Folder = 'linux-64'; Rid = 'linux-x64'; ExeSuffix = '' } }
        default { return $null }
    }
}

function Test-NativeAotBinary {
    param(
        [string] $Path,
        [string] $Rid
    )

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($Rid -eq 'win-x64') {
        if ($bytes.Length -lt 512 -or $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) {
            Fail "Expected a Windows PE executable: $Path"
        }

        $peOffset = [System.BitConverter]::ToInt32($bytes, 0x3C)
        if ($peOffset -lt 0 -or ($peOffset + 256) -ge $bytes.Length) {
            Fail "PE header is outside the file: $Path"
        }

        if ($bytes[$peOffset] -ne 0x50 -or $bytes[$peOffset + 1] -ne 0x45) {
            Fail "Missing PE signature: $Path"
        }

        $machine = [System.BitConverter]::ToUInt16($bytes, $peOffset + 4)
        if ($machine -ne 0x8664) {
            Fail "Expected x64 (0x8664) and found machine 0x$('{0:X4}' -f $machine): $Path"
        }

        $optional = $peOffset + 24
        $magic = [System.BitConverter]::ToUInt16($bytes, $optional)
        if ($magic -ne 0x20B) {
            Fail "Expected PE32+ and found magic 0x$('{0:X4}' -f $magic): $Path"
        }

        $clrSizeOffset = $optional + 112 + (14 * 8) + 4
        $clrSize = [System.BitConverter]::ToUInt32($bytes, $clrSizeOffset)
        if ($clrSize -ne 0) {
            Fail "Executable still has a CLR runtime header, so it is not Native AOT: $Path"
        }

        return
    }

    if ($bytes.Length -lt 64 -or $bytes[0] -ne 0x7F -or $bytes[1] -ne 0x45 -or $bytes[2] -ne 0x4C -or $bytes[3] -ne 0x46) {
        Fail "Expected a Linux ELF executable: $Path"
    }

    if ($bytes[4] -ne 2) {
        Fail "Expected a 64-bit ELF executable: $Path"
    }

    if ($bytes[5] -ne 1) {
        Fail "Expected a little-endian ELF executable: $Path"
    }

    $elfType = [System.BitConverter]::ToUInt16($bytes, 16)
    if ($elfType -ne 2 -and $elfType -ne 3) {
        Fail "Expected an ELF executable (ET_EXEC or ET_DYN) and found type ${elfType}: $Path"
    }

    $elfMachine = [System.BitConverter]::ToUInt16($bytes, 18)
    if ($elfMachine -ne 62) {
        Fail "Expected ELF x86-64 (62) and found machine ${elfMachine}: $Path"
    }
}

function Merge-PublishOutput {
    param(
        [string] $Source,
        [string] $Destination
    )

    $entries = @(Get-ChildItem -LiteralPath $Source -Force)
    foreach ($entry in $entries) {
        if ($entry.PSIsContainer) {
            Fail "Publish output '$($entry.FullName)' contains a directory. Release output must stay flat."
        }

        $target = Join-Path $Destination $entry.Name
        if (Test-Path -LiteralPath $target) {
            $sourceHash = (Get-FileHash -LiteralPath $entry.FullName -Algorithm SHA256).Hash
            $targetHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
            if ($sourceHash -eq $targetHash) {
                continue
            }

            $extension = [System.IO.Path]::GetExtension($entry.Name)
            if ($extension -eq '.pdb' -or $extension -eq '.dbg' -or $extension -eq '.xml') {
                continue
            }

            Fail "Publish file '$($entry.Name)' differs between applications and cannot share one release directory."
        }

        Copy-Item -LiteralPath $entry.FullName -Destination $target
    }
}

function Publish-Platform {
    param(
        [string] $Folder,
        [string] $Rid,
        [string] $ExeSuffix
    )

    $releaseDir = Join-Path (Join-Path $RepoRoot 'release') $Folder
    if (Test-Path -LiteralPath $releaseDir) {
        Remove-Item -LiteralPath $releaseDir -Recurse -Force
    }

    New-Item -ItemType Directory -Path $releaseDir | Out-Null
    Write-Host "Publishing $Rid Native AOT into $releaseDir"

    $stagingRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("vectornntp-release-" + [System.Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stagingRoot | Out-Null
    try {
        foreach ($app in $Applications) {
            if (-not (Test-Path -LiteralPath $app.Project)) {
                Fail "Project file was not found: $($app.Project)"
            }

            $stage = Join-Path $stagingRoot $app.Name
            Write-Host "dotnet publish $($app.Name) -c Release -r $Rid --self-contained true -p:PublishAot=true -o <staging>"
            & dotnet publish $app.Project -c Release -r $Rid --self-contained true -p:PublishAot=true -p:SelfContained=true -o $stage --nologo
            if ($LASTEXITCODE -ne 0) {
                Fail "dotnet publish failed for $($app.Name) ($Rid) with exit code $LASTEXITCODE."
            }

            $publishedExe = Join-Path $stage ($app.Exe + $ExeSuffix)
            if (-not (Test-Path -LiteralPath $publishedExe)) {
                Fail "Publish did not produce $($app.Exe)$ExeSuffix for $($app.Name)."
            }

            Merge-PublishOutput -Source $stage -Destination $releaseDir
        }
    }
    catch {
        if (Test-Path -LiteralPath $releaseDir) {
            Remove-Item -LiteralPath $releaseDir -Recurse -Force
        }

        throw
    }
    finally {
        if (Test-Path -LiteralPath $stagingRoot) {
            Remove-Item -LiteralPath $stagingRoot -Recurse -Force
        }
    }

    $nested = @(Get-ChildItem -LiteralPath $releaseDir -Directory -Force)
    if ($nested.Count -gt 0) {
        Fail "Release directory contains nested directories: $($nested.Name -join ', ')."
    }

    foreach ($app in $Applications) {
        $exePath = Join-Path $releaseDir ($app.Exe + $ExeSuffix)
        if (-not (Test-Path -LiteralPath $exePath)) {
            Fail "Missing release executable: $exePath"
        }

        $managedName = $app.Exe + '.dll'
        if ($app.Exe -eq 'NNTPCancelMessage') {
            $managedName = 'NNTPCancelMessage.dll'
        }

        $managedPath = Join-Path $releaseDir $managedName
        if (Test-Path -LiteralPath $managedPath) {
            Fail "Managed assembly '$managedName' is present. Native AOT output must not include it."
        }

        Test-NativeAotBinary -Path $exePath -Rid $Rid
    }

    foreach ($runtimeFile in @('hostfxr.dll', 'coreclr.dll', 'libhostfxr.so', 'libcoreclr.so', 'hostfxr.dylib', 'libcoreclr.dylib')) {
        $runtimePath = Join-Path $releaseDir $runtimeFile
        if (Test-Path -LiteralPath $runtimePath) {
            Fail "Shared runtime file '$runtimeFile' is present. The release is not a self-contained Native AOT layout."
        }
    }

    foreach ($jsonName in $RequiredJson) {
        $jsonPath = Join-Path $releaseDir $jsonName
        if (-not (Test-Path -LiteralPath $jsonPath)) {
            Fail "Missing configuration file: $jsonPath"
        }
    }

    $rabbitFiles = @(Get-ChildItem -LiteralPath $releaseDir -File -Filter 'RabbitMq.json' -Force)
    if ($rabbitFiles.Count -ne 1) {
        Fail "Expected exactly one RabbitMq.json in $releaseDir and found $($rabbitFiles.Count)."
    }

    Write-Host ""
    Write-Host "Release directory: $releaseDir"
    Get-ChildItem -LiteralPath $releaseDir -Force | Sort-Object Name | ForEach-Object {
        Write-Host ("{0,12}  {1}" -f $_.Length, $_.Name)
    }
}

try {
    if (-not (Test-Path -LiteralPath $Solution)) {
        Fail "Solution was not found at $Solution."
    }

    $selected = @()
    if ($Platform -eq 'all') {
        $selected = @('win-64', 'linux-64')
    }
    else {
        $mapped = Get-PlatformMap -Name $Platform
        if ($null -eq $mapped) {
            Fail "Unsupported platform '$Platform'. Use win-64, linux-64, or all."
        }

        $selected = @($Platform)
    }

    foreach ($name in $selected) {
        $mapped = Get-PlatformMap -Name $name
        Write-Host "Restoring $($mapped.Rid) assets"
        & dotnet restore $Solution -r $mapped.Rid --nologo
        if ($LASTEXITCODE -ne 0) {
            Fail "dotnet restore failed for $($mapped.Rid) with exit code $LASTEXITCODE."
        }

        Publish-Platform -Folder $mapped.Folder -Rid $mapped.Rid -ExeSuffix $mapped.ExeSuffix
    }
}
catch {
    [Console]::Error.WriteLine("error: $($_.Exception.Message)")
    exit 1
}
