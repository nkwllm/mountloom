[CmdletBinding()]
param()

. (Join-Path $PSScriptRoot 'common.ps1')
& (Join-Path $PSScriptRoot 'bootstrap.ps1') -IncludeRclone
$rclone = Join-Path $script:MountloomRoot '.tools/rclone/rclone-v1.75.2-windows-amd64/rclone.exe'
Write-Host 'Explicit local integration test: requires WinFsp, starts a loopback WebDAV server, and uses one unused drive letter.'
Write-Host 'Only synthetic files are used. Temporary test data/cache are retained for inspection. No driver is installed by this script.'
Invoke-MountloomDotnet -Arguments @('run', '--project', 'tests/Mountloom.Integration/Mountloom.Integration.csproj',
    '--configuration', 'Release', '--', '--rclone', $rclone)
