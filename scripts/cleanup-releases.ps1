[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string[]]$KeepPaths
)

. (Join-Path $PSScriptRoot 'common.ps1')
$artifacts = Assert-MountloomChildPath (Join-Path $script:MountloomRoot 'artifacts')
$keep = @($KeepPaths | ForEach-Object { Assert-MountloomChildPath $_ })
foreach ($path in $keep) {
    if (-not (Test-Path -LiteralPath $path)) { throw "The retained release does not exist: $path" }
}

# Refuse cleanup when process ownership cannot be checked. Never terminate an app or
# a mount to make an old publication removable; rerun after normal app shutdown.
try {
    $running = @(Get-Process -Name Mountloom.App, Mountly.App, rclone, Mountloom-Setup-x64*, Mountly-Setup-x64* -ErrorAction SilentlyContinue)
    $runningPaths = @($running | ForEach-Object {
        if ([string]::IsNullOrWhiteSpace($_.Path)) { throw 'A running Mountloom/Mountly/rclone or installer process path is unavailable.' }
        [IO.Path]::GetFullPath($_.Path)
    })
}
catch {
    Write-Warning "Old release cleanup was skipped because running process paths could not be verified. $($_.Exception.Message)"
    return
}

$groups = @{}
$releaseDirectories = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($item in Get-ChildItem -LiteralPath $artifacts -Force) {
    $base = $null
    if ($item.PSIsContainer) {
        # Only recognized publications qualify; copied test fixtures and extracted
        # installer payloads are not release directories, even if they contain the app.
        if (((Test-Path -LiteralPath (Join-Path $item.FullName 'Mountloom.App.exe') -PathType Leaf) -or
             (Test-Path -LiteralPath (Join-Path $item.FullName 'Mountly.App.exe') -PathType Leaf)) -and
            (Test-Path -LiteralPath (Join-Path $item.FullName 'build-manifest.json') -PathType Leaf)) {
            if ($item.Name -match '^(?:Mountloom|Mountly)-win-x64(?:[._-][A-Za-z0-9._-]+)?$') { $base = $item.Name }
            else {
                try {
                    $manifest = Get-Content -LiteralPath (Join-Path $item.FullName 'build-manifest.json') -Raw | ConvertFrom-Json
                    if ($manifest.PSObject.Properties['product'] -and $manifest.product -in @('Mountloom', 'Mountly') -and
                        $manifest.PSObject.Properties['outputName'] -and $manifest.outputName -eq $item.Name) {
                        $base = $item.Name
                    }
                }
                catch { continue }
            }
        }
    }
    elseif ($item.Name -match '^((?:Mountloom|Mountly)-win-x64(?:[._-][A-Za-z0-9._-]+)?)\.zip(?:\.sha256)?$') {
        $base = $Matches[1]
    }
    if ($null -ne $base) {
        if ($item.PSIsContainer) { [void]$releaseDirectories.Add($item.FullName) }
        $groups['portable:' + $base] = @(
            (Join-Path $artifacts $base),
            (Join-Path $artifacts ($base + '.zip')),
            (Join-Path $artifacts ($base + '.zip.sha256')))
    }

    $base = $null
    if (-not $item.PSIsContainer -and
        $item.Name -match '^((?:Mountloom|Mountly)-Setup-x64(?:[._-][A-Za-z0-9._-]+)?)(?:\.exe(?:\.sha256)?|\.manifest\.json)$') {
        $base = $Matches[1]
    }
    elseif (-not $item.PSIsContainer -and $item.Name.EndsWith('.manifest.json', [StringComparison]::OrdinalIgnoreCase)) {
        try {
            $manifest = Get-Content -LiteralPath $item.FullName -Raw | ConvertFrom-Json
            $manifestBase = $item.Name.Substring(0, $item.Name.Length - '.manifest.json'.Length)
            if ($manifest.PSObject.Properties['product'] -and $manifest.product -in @('Mountloom', 'Mountly') -and
                $manifest.PSObject.Properties['outputName'] -and $manifest.outputName -eq $manifestBase) {
                $base = $manifestBase
            }
        }
        catch { continue }
    }
    if ($null -ne $base) {
        $groups['installer:' + $base] = @(
            (Join-Path $artifacts ($base + '.exe')),
            (Join-Path $artifacts ($base + '.exe.sha256')),
            (Join-Path $artifacts ($base + '.manifest.json')))
    }
}

function Test-RetainedReleaseGroup {
    param([string[]]$Paths)
    return @($Paths | Where-Object {
        $candidate = $_
        @($keep | Where-Object {
            $_.Equals($candidate, [StringComparison]::OrdinalIgnoreCase) -or
            $_.StartsWith($candidate + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
        }).Count -ne 0
    }).Count -ne 0
}
if (@($groups.Values | Where-Object { Test-RetainedReleaseGroup -Paths $_ }).Count -eq 0) {
    throw 'KeepPaths must identify an existing Mountloom or Mountly release directory, archive or installer; no cleanup was performed.'
}

foreach ($paths in $groups.Values) {
    if (Test-RetainedReleaseGroup -Paths $paths) { continue }
    $active = @($runningPaths | Where-Object {
        $executable = $_
        @($paths | Where-Object {
            $executable.Equals($_, [StringComparison]::OrdinalIgnoreCase) -or
            $executable.StartsWith($_ + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
        }).Count -ne 0
    })
    if ($active.Count -ne 0) {
        Write-Warning "Old release is still in use and was retained: $($paths[0]). Exit Mountloom or Mountly normally, then rerun scripts/cleanup-releases.ps1 with -KeepPaths naming the current release."
        continue
    }
    foreach ($path in $paths) {
        if (-not (Test-Path -LiteralPath $path)) { continue }
        try {
            $checked = Assert-MountloomChildPath $path
            if ((Test-Path -LiteralPath $checked -PathType Container) -and -not $releaseDirectories.Contains($checked)) {
                Write-Warning "Directory is not a recognized generated release and was retained: $checked"
                continue
            }
            if ($PSCmdlet.ShouldProcess($checked, 'Remove obsolete generated Mountloom release')) {
                Remove-Item -LiteralPath $checked -Recurse -Force
            }
        }
        catch { Write-Warning "Could not remove old release output '$path': $($_.Exception.Message)" }
    }
}
