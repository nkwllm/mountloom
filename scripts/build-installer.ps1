[CmdletBinding()]
param(
    [string]$AppDirectory,
    [switch]$SkipTests,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')][string]$OutputName = 'Mountloom-Setup-x64'
)

. (Join-Path $PSScriptRoot 'common.ps1')

# Pinned upstream releases. See installer/README.md for provenance and installation behavior.
$innoVersion = '7.1.0'
$innoHash = '0362a383ed217d4c4239b5933866dd96d3eb2102737da92f80f6057a4b40df2f'
$compilerHash = 'd06ebd38f38e3cee60a3c50cc45bd449d77e0bc6a5cabc607ea9886808e4de1a'
$winFspVersion = '2.2.26215'
$winFspHash = '2ecb5c89405488a95bbd8a01875e02c48534fd37bbdfd84488f7590464d65944'
$rcloneHash = '41eab3e118d264920b62c3327966c5ece0dc4dd715ee617d9e30450d8ead95a4'

function Get-InstallerDownload {
    param([string]$Name, [string]$Uri, [string]$Sha256)
    $downloads = Assert-MountloomChildPath (Join-Path $script:MountloomRoot '.tools/downloads')
    New-Item -ItemType Directory -Path $downloads -Force | Out-Null
    $target = Assert-MountloomChildPath (Join-Path $downloads $Name)
    if ((Test-Path -LiteralPath $target -PathType Leaf) -and
        (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -eq $Sha256) { return $target }
    $partial = Assert-MountloomChildPath ($target + '.' + [Guid]::NewGuid().ToString('N') + '.partial')
    try {
        Write-Host "Downloading pinned installer dependency: $Name"
        Invoke-WebRequest -Uri $Uri -OutFile $partial -UseBasicParsing
        if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $Sha256) {
            throw "SHA-256 mismatch for $Name. The unverified download will not be used."
        }
        Move-Item -LiteralPath (Assert-MountloomChildPath $partial) -Destination $target -Force
        return $target
    }
    finally {
        if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath (Assert-MountloomChildPath $partial) -Force }
    }
}

if ([string]::IsNullOrWhiteSpace($AppDirectory)) {
    & (Join-Path $PSScriptRoot 'publish.ps1') -SkipTests:$SkipTests
    $AppDirectory = Join-Path $script:MountloomRoot 'artifacts/Mountloom-win-x64'
}
$app = Assert-MountloomChildPath (Resolve-Path -LiteralPath $AppDirectory).Path
foreach ($name in @('Mountloom.App.exe', 'Mountloom.App.dll', 'Mountloom.App.runtimeconfig.json',
        'hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'PresentationFramework.dll', 'rclone.exe',
        'build-manifest.json', 'LICENSE', 'third-party/rclone/LICENSE.txt', 'third-party/dotnet/LICENSE.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $app $name) -PathType Leaf)) {
        throw "The application directory is not a complete self-contained publication: missing $name. Run scripts/publish.ps1."
    }
}
if ((Get-FileHash -LiteralPath (Join-Path $app 'rclone.exe') -Algorithm SHA256).Hash -ne $rcloneHash) {
    throw 'The publication does not contain the verified rclone 1.75.2 executable. Run scripts/publish.ps1.'
}
$manifest = Get-Content -LiteralPath (Join-Path $app 'build-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.runtime -ne 'win-x64' -or $manifest.selfContained -ne $true -or $manifest.rclone -ne '1.75.2') {
    throw 'The publication manifest does not describe a self-contained win-x64 package with rclone 1.75.2.'
}
$appVersion = (Get-Item -LiteralPath (Join-Path $app 'Mountloom.App.dll')).VersionInfo.FileVersion
if ($appVersion -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw "Invalid application version: $appVersion" }
$packageCheck = Join-Path $PSScriptRoot 'test-package.ps1'
if (Test-Path -LiteralPath $packageCheck) { & $packageCheck -AppDirectory $app }

$innoInstaller = Get-InstallerDownload "innosetup-$innoVersion-x64.exe" `
    "https://github.com/jrsoftware/issrc/releases/download/is-7_1_0/innosetup-$innoVersion-x64.exe" $innoHash
$winFspInstaller = Get-InstallerDownload "winfsp-$winFspVersion.msi" `
    "https://github.com/winfsp/winfsp/releases/download/v2.2B4/winfsp-$winFspVersion.msi" $winFspHash

# Inno's own portable mode puts the build tool in this checkout, without an uninstaller,
# system PATH changes, file associations, shortcuts, or an Add/Remove Programs entry.
$compilerDirectory = Assert-MountloomChildPath (Join-Path $script:MountloomRoot ".tools/innosetup/$innoVersion-portable")
$compiler = Join-Path $compilerDirectory 'ISCC.exe'
$extractedCompiler = Assert-MountloomChildPath (Join-Path $script:MountloomRoot ".tools/innosetup/$innoVersion/{app}/ISCC.exe")
if ((Test-Path -LiteralPath $extractedCompiler -PathType Leaf) -and
    (Get-FileHash -LiteralPath $extractedCompiler -Algorithm SHA256).Hash -eq $compilerHash) {
    $compiler = $extractedCompiler
}
elseif (-not (Test-Path -LiteralPath $compiler -PathType Leaf) -or
    (Get-FileHash -LiteralPath $compiler -Algorithm SHA256).Hash -ne $compilerHash) {
    New-Item -ItemType Directory -Path $compilerDirectory -Force | Out-Null
    $innoArguments = @('/SP-', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER',
        '/PORTABLE=1', '/NOICONS', '/TASKS=', ('/DIR="' + $compilerDirectory + '"'))
    $process = Start-Process -FilePath $innoInstaller -ArgumentList $innoArguments -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -ne 0) { throw "Portable Inno Setup preparation failed with exit code $($process.ExitCode)." }
}
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf) -or
    (Get-FileHash -LiteralPath $compiler -Algorithm SHA256).Hash -ne $compilerHash) {
    throw 'The pinned Inno Setup compiler could not be verified.'
}

$artifacts = Assert-MountloomChildPath (Join-Path $script:MountloomRoot 'artifacts')
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
$stage = Assert-MountloomChildPath (Join-Path $artifacts ('.installer-' + [Guid]::NewGuid().ToString('N')))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
try {
    $installerManifest = [ordered]@{
        product = 'Mountloom'; outputName = $OutputName
        appVersion = $appVersion; runtime = 'win-x64'; selfContained = $true
        rclone = '1.75.2'; rcloneSha256 = $rcloneHash
        winFspBundled = $true; winFsp = $winFspVersion; winFspRelease = 'v2.2B4'
        winFspSha256 = $winFspHash; compiler = "Inno Setup $innoVersion"
        appAssemblySha256 = (Get-FileHash -LiteralPath (Join-Path $app 'Mountloom.App.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $manifestPath = Join-Path $stage 'installer-manifest.json'
    [IO.File]::WriteAllText($manifestPath, ($installerManifest | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    $arguments = @('/Qp', "/DAppSource=$app", "/DAppVersion=$appVersion",
        "/DWinFspMsi=$winFspInstaller", "/DWinFspSha256=$winFspHash",
        "/DInstallerManifest=$manifestPath", "/O$stage", "/F$OutputName",
        (Join-Path $script:MountloomRoot 'installer/Mountloom.iss'))
    & $compiler @arguments
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed with exit code $LASTEXITCODE." }
    $stageInstaller = Join-Path $stage ($OutputName + '.exe')
    if (-not (Test-Path -LiteralPath $stageInstaller -PathType Leaf)) { throw 'The compiler did not produce the expected installer.' }
    $checksum = (Get-FileHash -LiteralPath $stageInstaller -Algorithm SHA256).Hash.ToLowerInvariant()
    $output = Assert-MountloomChildPath (Join-Path $artifacts ($OutputName + '.exe'))
    Move-Item -LiteralPath (Assert-MountloomChildPath $stageInstaller) -Destination $output -Force
    [IO.File]::WriteAllText($output + '.sha256', $checksum + '  ' + [IO.Path]::GetFileName($output) + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
    Copy-Item -LiteralPath $manifestPath -Destination (Assert-MountloomChildPath (Join-Path $artifacts ($OutputName + '.manifest.json'))) -Force
    & (Join-Path $PSScriptRoot 'cleanup-releases.ps1') -KeepPaths @(
        $app, $output, ($output + '.sha256'), (Join-Path $artifacts ($OutputName + '.manifest.json')))
    Write-Host "Installer: $output"
    Write-Host "SHA-256:   $checksum"
    Write-Host 'The installer includes the app, .NET runtime, rclone and WinFsp. Building does not install the app or WinFsp.'
}
finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath (Assert-MountloomChildPath $stage) -Recurse -Force }
}
