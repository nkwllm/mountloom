[CmdletBinding()]
param([switch]$IncludeRclone)

. (Join-Path $PSScriptRoot 'common.ps1')
if ($env:OS -ne 'Windows_NT' -or -not [Environment]::Is64BitOperatingSystem) {
    throw 'This bootstrap currently supports Windows x64 only.'
}

$sdkVersion = '10.0.402'
$rcloneVersion = '1.75.2'
$sdkHash = 'ae4fd528cb7e9f6af6aaf7de36715a69f761f183744f1bb3025d19f5a92447090ec561410e46266c1d10a63781ed6f3de771760bc76c876d87daabc367b898cd'
$rcloneHash = '8a9db2bad1dc79a65b3348fd857069598e2aab7cf4d118b77b1df23055aff818'
$rcloneExecutableHash = '41eab3e118d264920b62c3327966c5ece0dc4dd715ee617d9e30450d8ead95a4'
$sdkSettings = Get-Content -LiteralPath (Join-Path $script:MountloomRoot 'global.json') -Raw | ConvertFrom-Json
if ($sdkSettings.sdk.version -ne $sdkVersion) { throw 'Update the bootstrap SDK URL and checksum together with global.json.' }
$toolsDirectory = Assert-MountloomChildPath (Join-Path $script:MountloomRoot '.tools')
$downloads = Assert-MountloomChildPath (Join-Path $toolsDirectory 'downloads')
New-Item -ItemType Directory -Path $downloads -Force | Out-Null

function Get-VerifiedArchive {
    param([string]$Url, [string]$FileName, [string]$Algorithm, [string]$ExpectedHash)
    $archive = Assert-MountloomChildPath (Join-Path $downloads $FileName)
    if (Test-Path -LiteralPath $archive -PathType Leaf) {
        if ((Get-FileHash -LiteralPath $archive -Algorithm $Algorithm).Hash -eq $ExpectedHash) { return $archive }
        Remove-Item -LiteralPath $archive -Force
    }
    $partial = Assert-MountloomChildPath ($archive + '.' + [Guid]::NewGuid().ToString('N') + '.download')
    try {
        Write-Host "Downloading $FileName to the project-local .tools directory..."
        Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $partial
        if ((Get-FileHash -LiteralPath $partial -Algorithm $Algorithm).Hash -ne $ExpectedHash) {
            throw "$FileName failed $Algorithm verification. Nothing was installed."
        }
        Move-Item -LiteralPath $partial -Destination $archive
        return $archive
    }
    finally { if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force } }
}

function Expand-VerifiedArchive {
    param([string]$Archive, [string]$Destination)
    $Destination = Assert-MountloomChildPath $Destination
    $staging = Assert-MountloomChildPath (Join-Path $toolsDirectory ('.extract-' + [Guid]::NewGuid().ToString('N')))
    try {
        New-Item -ItemType Directory -Path $staging | Out-Null
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::ExtractToDirectory($Archive, $staging)
        if (Test-Path -LiteralPath $Destination) {
            $checked = Assert-MountloomChildPath $Destination
            Remove-Item -LiteralPath $checked -Recurse -Force
        }
        Move-Item -LiteralPath $staging -Destination $Destination
    }
    finally {
        if (Test-Path -LiteralPath $staging) {
            $checked = Assert-MountloomChildPath $staging
            Remove-Item -LiteralPath $checked -Recurse -Force
        }
    }
}

$dotnetDirectory = Join-Path $toolsDirectory 'dotnet'
$dotnet = Join-Path $dotnetDirectory 'dotnet.exe'
$sdkReady = $false
if (Test-Path -LiteralPath $dotnet -PathType Leaf) {
    Push-Location $script:MountloomRoot
    try {
        $activeVersion = & $dotnet --version 2>$null
        $sdkReady = $LASTEXITCODE -eq 0 -and $activeVersion -eq $sdkVersion
    }
    finally { Pop-Location }
}
if (-not $sdkReady) {
    $archive = Get-VerifiedArchive "https://builds.dotnet.microsoft.com/dotnet/Sdk/$sdkVersion/dotnet-sdk-$sdkVersion-win-x64.zip" "dotnet-sdk-$sdkVersion-win-x64.zip" 'SHA512' $sdkHash
    Expand-VerifiedArchive $archive $dotnetDirectory
}
Write-Host "Local .NET SDK ready: $dotnetDirectory ($sdkVersion)"

if ($IncludeRclone) {
    $rcloneDirectory = Join-Path $toolsDirectory 'rclone'
    $rclone = Join-Path $rcloneDirectory "rclone-v$rcloneVersion-windows-amd64\rclone.exe"
    $rcloneReady = $false
    if (Test-Path -LiteralPath $rclone -PathType Leaf) {
        # Verify the pinned binary without starting rclone or reading user rclone options.
        $rcloneReady = (Get-FileHash -LiteralPath $rclone -Algorithm SHA256).Hash -eq $rcloneExecutableHash -and
            (Test-Path -LiteralPath (Join-Path (Split-Path $rclone) 'README.txt') -PathType Leaf) -and
            (Test-Path -LiteralPath (Join-Path (Split-Path $rclone) 'git-log.txt') -PathType Leaf)
    }
    if (-not $rcloneReady) {
        $archive = Get-VerifiedArchive "https://downloads.rclone.org/v$rcloneVersion/rclone-v$rcloneVersion-windows-amd64.zip" "rclone-v$rcloneVersion-windows-amd64.zip" 'SHA256' $rcloneHash
        Expand-VerifiedArchive $archive $rcloneDirectory
    }
    Write-Host "Local rclone ready: $rclone ($rcloneVersion)"
}
