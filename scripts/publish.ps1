[CmdletBinding()]
param(
    [switch]$SkipTests,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')][string]$OutputName = 'Mountloom-win-x64'
)

. (Join-Path $PSScriptRoot 'common.ps1')
$projectLicense = Join-Path $script:MountloomRoot 'LICENSE'
if (-not (Test-Path -LiteralPath $projectLicense -PathType Leaf)) {
    throw 'The project GPL-3.0 license is required before publishing.'
}
$packageReadme = Join-Path $script:MountloomRoot 'docs/PACKAGE-README.md'
if (-not (Test-Path -LiteralPath $packageReadme -PathType Leaf)) {
    throw 'The package README is required before publishing.'
}
& (Join-Path $PSScriptRoot 'bootstrap.ps1') -IncludeRclone
if (-not $SkipTests) { & (Join-Path $PSScriptRoot 'build.ps1') -Configuration Release -Test }

$artifacts = Assert-MountloomChildPath (Join-Path $script:MountloomRoot 'artifacts')
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
$stageRoot = Assert-MountloomChildPath (Join-Path $artifacts ('.publish-' + [Guid]::NewGuid().ToString('N')))
$stageApp = Assert-MountloomChildPath (Join-Path $stageRoot $OutputName)
$output = Assert-MountloomChildPath (Join-Path $artifacts $OutputName)
$zip = Assert-MountloomChildPath (Join-Path $artifacts ($OutputName + '.zip'))
function Assert-PublicationNotRunning {
    foreach ($process in @(Get-Process -Name Mountloom.App, Mountly.App, rclone -ErrorAction SilentlyContinue)) {
        if ([string]::IsNullOrWhiteSpace($process.Path)) {
            throw 'Cannot verify the directory of a running Mountloom/Mountly/rclone process; publication replacement was stopped.'
        }
        if ([IO.Path]::GetFullPath($process.Path).StartsWith($output + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "This output is in use by Mountloom, Mountly or rclone. Exit the application normally before replacing it: $output"
        }
    }
}
Assert-PublicationNotRunning
try {
    Invoke-MountloomDotnet -Arguments @('publish', 'src/Mountloom.App/Mountloom.App.csproj', '--configuration', 'Release',
        '--runtime', 'win-x64', '--self-contained', 'true', '--output', $stageApp,
        '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-p:ContinuousIntegrationBuild=true',
        '-p:DebugType=None', '-p:DebugSymbols=false')
    $upstream = Join-Path $script:MountloomRoot '.tools/rclone/rclone-v1.75.2-windows-amd64'
    Copy-Item -LiteralPath (Join-Path $upstream 'rclone.exe') -Destination (Join-Path $stageApp 'rclone.exe')
    $notices = Join-Path $stageApp 'third-party/rclone'
    New-Item -ItemType Directory -Path $notices -Force | Out-Null
    foreach ($name in @('README.txt', 'git-log.txt')) {
        Copy-Item -LiteralPath (Join-Path $upstream $name) -Destination (Join-Path $notices $name)
    }
    $readme = [IO.File]::ReadAllText((Join-Path $upstream 'README.txt'))
    $license = [regex]::Match($readme, '(?ms)^License\r?\n.*?(?=^Authors and contributors\r?$)')
    if (-not $license.Success -or $license.Value -notmatch 'Permission is hereby granted') {
        throw 'The pinned rclone archive no longer has the expected original license section.'
    }
    [IO.File]::WriteAllText((Join-Path $notices 'LICENSE.txt'), $license.Value, [Text.UTF8Encoding]::new($false))
    $dotnetNotices = Join-Path $stageApp 'third-party/dotnet'
    New-Item -ItemType Directory -Path $dotnetNotices -Force | Out-Null
    foreach ($name in @('LICENSE.txt', 'ThirdPartyNotices.txt')) {
        Copy-Item -LiteralPath (Join-Path $script:MountloomRoot ".tools/dotnet/$name") -Destination (Join-Path $dotnetNotices $name)
    }
    # Preserve the fixed runtime components' own licenses as well as SDK distribution terms.
    Copy-Item -Path (Join-Path $script:MountloomRoot 'third-party/dotnet/*') -Destination $dotnetNotices -Recurse
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $stageApp 'Mountloom.App.runtimeconfig.json') -Raw | ConvertFrom-Json
    foreach ($framework in $runtimeConfig.runtimeOptions.includedFrameworks) {
        if ($framework.version -ne '10.0.13') {
            throw 'Update third-party/dotnet licenses and sources for the new bundled runtime before publishing.'
        }
    }
    Copy-Item -LiteralPath $packageReadme -Destination (Join-Path $stageApp 'README.md')
    Copy-Item -LiteralPath (Join-Path $script:MountloomRoot 'THIRD-PARTY-NOTICES.md') -Destination (Join-Path $stageApp 'THIRD-PARTY-NOTICES.md')
    Copy-Item -LiteralPath $projectLicense -Destination (Join-Path $stageApp 'LICENSE')
    $manifest = [ordered]@{
        product = 'Mountloom'; outputName = $OutputName
        runtime = 'win-x64'; selfContained = $true; sdk = '10.0.402'; rclone = '1.75.2'
        applicationVersion = ([xml](Get-Content -LiteralPath (Join-Path $script:MountloomRoot 'src/Mountloom.App/Mountloom.App.csproj') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ }
        rcloneManagedAutomatically = $true
        winFspBundled = $false
        rcloneSha256 = (Get-FileHash -LiteralPath (Join-Path $stageApp 'rclone.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
        applicationSha256 = (Get-FileHash -LiteralPath (Join-Path $stageApp 'Mountloom.App.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
        coreSha256 = (Get-FileHash -LiteralPath (Join-Path $stageApp 'Mountloom.Core.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
        iconSha256 = (Get-FileHash -LiteralPath (Join-Path $stageApp 'Mountloom.ico') -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    [IO.File]::WriteAllText((Join-Path $stageApp 'build-manifest.json'), ($manifest | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    & (Join-Path $PSScriptRoot 'test-package.ps1') -AppDirectory $stageApp
    $stageZip = Join-Path $stageRoot ($OutputName + '.zip')
    Compress-Archive -LiteralPath $stageApp -DestinationPath $stageZip -CompressionLevel Optimal
    # Replace only known generated outputs after publication and archive creation succeed.
    Assert-PublicationNotRunning
    if (Test-Path -LiteralPath $output) {
        $checked = Assert-MountloomChildPath $output
        Remove-Item -LiteralPath $checked -Recurse -Force
    }
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath (Assert-MountloomChildPath $zip) -Force }
    Move-Item -LiteralPath (Assert-MountloomChildPath $stageApp) -Destination $output
    Move-Item -LiteralPath (Assert-MountloomChildPath $stageZip) -Destination $zip
    $checksum = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($zip + '.sha256', $checksum + '  ' + [IO.Path]::GetFileName($zip) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    & (Join-Path $PSScriptRoot 'cleanup-releases.ps1') -KeepPaths @($output, $zip, ($zip + '.sha256'))
    Write-Host "Portable folder: $output"
    Write-Host "Portable archive: $zip"
    Write-Host 'WinFsp is a separate installation prerequisite. App settings and cache still use the current user profile.'
}
finally {
    if (Test-Path -LiteralPath $stageRoot) {
        $checked = Assert-MountloomChildPath $stageRoot
        Remove-Item -LiteralPath $checked -Recurse -Force
    }
}
