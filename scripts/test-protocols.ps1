[CmdletBinding()]
param()

. (Join-Path $PSScriptRoot 'common.ps1')
& (Join-Path $PSScriptRoot 'bootstrap.ps1') -IncludeRclone
$rclone = Join-Path $script:MountloomRoot '.tools/rclone/rclone-v1.75.2-windows-amd64/rclone.exe'
Write-Host 'Local SFTP/S3 protocol smoke: uses loopback servers, temporary keys and synthetic data; no drive is mounted.'
Write-Host 'Requires the Windows OpenSSH ssh-keygen and ssh-keyscan executables. No existing user SSH keys are used.'
Invoke-MountloomDotnet -Arguments @('run', '--project', 'tests/Mountloom.ProtocolIntegration/Mountloom.ProtocolIntegration.csproj',
    '--configuration', 'Release', '--', $rclone)
