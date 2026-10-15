[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$Test
)

. (Join-Path $PSScriptRoot 'common.ps1')
& (Join-Path $PSScriptRoot 'bootstrap.ps1')
Invoke-MountloomDotnet -Arguments @('restore', 'Mountloom.sln')
Invoke-MountloomDotnet -Arguments @('build', 'Mountloom.sln', '--configuration', $Configuration, '--no-restore', '-p:ContinuousIntegrationBuild=true')
if ($Test) {
    Invoke-MountloomDotnet -Arguments @('test', 'tests/Mountloom.Tests/Mountloom.Tests.csproj', '--configuration', $Configuration,
        '--no-build', '--no-restore', '--logger', 'trx;LogFileName=Mountloom.Tests.trx', '--results-directory', 'artifacts/TestResults')
}
