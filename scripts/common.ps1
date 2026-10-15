Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:MountloomRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Assert-MountloomChildPath {
    param([Parameter(Mandatory)][string]$Path)
    $full = [IO.Path]::GetFullPath($Path)
    $prefix = $script:MountloomRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing a generated-file operation outside this checkout: $full"
    }
    $cursor = $full
    while ($cursor.Length -ge $script:MountloomRoot.Length) {
        if (Test-Path -LiteralPath $cursor) {
            if (((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing a generated-file operation through a reparse point: $cursor"
            }
        }
        $parent = [IO.Path]::GetDirectoryName($cursor)
        if ([string]::IsNullOrEmpty($parent) -or $parent -eq $cursor) { break }
        $cursor = $parent
    }
    return $full
}

function Invoke-MountloomDotnet {
    param([Parameter(Mandatory)][string[]]$Arguments)
    $dotnetDirectory = Join-Path $script:MountloomRoot '.tools\dotnet'
    $dotnet = Join-Path $dotnetDirectory 'dotnet.exe'
    if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
        throw 'Run scripts/bootstrap.ps1 first.'
    }
    $changes = @{
        DOTNET_ROOT = $dotnetDirectory
        DOTNET_ROOT_X64 = $dotnetDirectory
        DOTNET_MULTILEVEL_LOOKUP = '0'
        DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        DOTNET_NOLOGO = '1'
    }
    $previous = @{}
    foreach ($name in $changes.Keys) {
        $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $changes[$name], 'Process')
    }
    Push-Location $script:MountloomRoot
    try {
        & $dotnet @Arguments
        if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE." }
    }
    finally {
        Pop-Location
        foreach ($name in $changes.Keys) {
            [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process')
        }
    }
}
