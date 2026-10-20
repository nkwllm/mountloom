[CmdletBinding()]
param([Parameter(Mandatory)][string]$AppDirectory)

. (Join-Path $PSScriptRoot 'common.ps1')
$app = [IO.Path]::GetFullPath($AppDirectory)

# Installer input is copied recursively. Accept only the publication layout, so
# local settings, credentials, logs and development files cannot be bundled by
# passing an otherwise valid application directory to build-installer.ps1.
$rootFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($name in @('Mountloom.App.exe', 'rclone.exe', 'createdump.exe', 'build-manifest.json',
    'Mountloom.App.deps.json', 'Mountloom.App.runtimeconfig.json', 'README.md', 'LICENSE', 'LICENSE.txt',
    'THIRD-PARTY-NOTICES.md', 'Mountloom.ico')) { [void]$rootFiles.Add($name) }
$noticeFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($name in @('third-party/rclone/LICENSE.txt', 'third-party/rclone/README.txt', 'third-party/rclone/git-log.txt',
    'third-party/dotnet/LICENSE.txt', 'third-party/dotnet/ThirdPartyNotices.txt',
    'third-party/dotnet/SOURCE.txt',
    'third-party/dotnet/runtime/LICENSE.TXT', 'third-party/dotnet/runtime/THIRD-PARTY-NOTICES.TXT',
    'third-party/dotnet/wpf/LICENSE.TXT', 'third-party/dotnet/wpf/THIRD-PARTY-NOTICES.TXT',
    'third-party/dotnet/winforms/LICENSE.TXT', 'third-party/dotnet/winforms/THIRD-PARTY-NOTICES.TXT',
    'third-party/dotnet/protecteddata/LICENSE.TXT', 'third-party/dotnet/protecteddata/THIRD-PARTY-NOTICES.TXT',
    'third-party/winfsp/LICENSE.txt', 'third-party/winfsp/SOURCE.txt')) { [void]$noticeFiles.Add($name) }
$cultures = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($culture in [Globalization.CultureInfo]::GetCultures([Globalization.CultureTypes]::AllCultures)) {
    if ($culture.Name) { [void]$cultures.Add($culture.Name) }
}
$packageRoot = Get-Item -LiteralPath $app -Force
if (-not $packageRoot.PSIsContainer -or ($packageRoot.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'The package root must be an ordinary directory, not a redirected path.'
}
$directories = [Collections.Generic.Queue[IO.DirectoryInfo]]::new()
$directories.Enqueue($packageRoot)
while ($directories.Count -gt 0) {
    $directory = $directories.Dequeue()
    foreach ($entry in Get-ChildItem -LiteralPath $directory.FullName -Force) {
        $relative = $entry.FullName.Substring($packageRoot.FullName.Length).TrimStart('\', '/').Replace('\', '/')
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "The package contains a redirected path: $relative"
        }
        $parts = $relative.Split('/')
        if ($entry.PSIsContainer) {
            $allowed = ($parts.Length -eq 1 -and ($relative -eq 'third-party' -or $cultures.Contains($relative))) -or
                $relative -in @('third-party/rclone', 'third-party/dotnet', 'third-party/winfsp',
                    'third-party/dotnet/runtime', 'third-party/dotnet/wpf', 'third-party/dotnet/winforms',
                    'third-party/dotnet/protecteddata')
            if (-not $allowed) { throw "Unexpected directory in publication: $relative. Publish a clean application directory." }
            $directories.Enqueue($entry)
        }
        else {
            $allowed = ($parts.Length -eq 1 -and ($rootFiles.Contains($relative) -or $entry.Extension -eq '.dll')) -or
                $noticeFiles.Contains($relative) -or
                ($parts.Length -eq 2 -and $cultures.Contains($parts[0]) -and $entry.Name -like '*.resources.dll')
            if (-not $allowed) { throw "Unexpected file in publication: $relative. Remove private data and development output before packaging." }
        }
    }
}

foreach ($name in @('Mountloom.App.exe', 'Mountloom.App.dll', 'Mountloom.Core.dll', 'Mountloom.App.deps.json',
    'Mountloom.App.runtimeconfig.json', 'hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'PresentationFramework.dll',
    'rclone.exe', 'Mountloom.ico', 'build-manifest.json', 'LICENSE', 'third-party/rclone/LICENSE.txt', 'third-party/rclone/README.txt',
    'third-party/dotnet/LICENSE.txt', 'third-party/dotnet/ThirdPartyNotices.txt', 'THIRD-PARTY-NOTICES.md',
    'third-party/dotnet/SOURCE.txt',
    'third-party/dotnet/runtime/LICENSE.TXT', 'third-party/dotnet/runtime/THIRD-PARTY-NOTICES.TXT',
    'third-party/dotnet/wpf/LICENSE.TXT', 'third-party/dotnet/wpf/THIRD-PARTY-NOTICES.TXT',
    'third-party/dotnet/winforms/LICENSE.TXT', 'third-party/dotnet/winforms/THIRD-PARTY-NOTICES.TXT',
    'third-party/dotnet/protecteddata/LICENSE.TXT', 'third-party/dotnet/protecteddata/THIRD-PARTY-NOTICES.TXT')) {
    if (-not (Test-Path -LiteralPath (Join-Path $app $name) -PathType Leaf)) {
        throw "The published application is incomplete: $name"
    }
}
$manifest = Get-Content -LiteralPath (Join-Path $app 'build-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.runtime -ne 'win-x64' -or $manifest.selfContained -ne $true -or $manifest.rclone -ne '1.75.2') {
    throw 'The published application must contain the pinned Windows x64 runtime and rclone.'
}
$runtime = Get-Content -LiteralPath (Join-Path $app 'Mountloom.App.runtimeconfig.json') -Raw | ConvertFrom-Json
$frameworks = @($runtime.runtimeOptions.includedFrameworks | ForEach-Object { $_.name })
if ('Microsoft.NETCore.App' -notin $frameworks -or 'Microsoft.WindowsDesktop.App' -notin $frameworks) {
    throw 'The application does not declare both bundled .NET and Windows Desktop runtimes.'
}
$rclone = Join-Path $app 'rclone.exe'
$hash = (Get-FileHash -LiteralPath $rclone -Algorithm SHA256).Hash.ToLowerInvariant()
if ($hash -ne '41eab3e118d264920b62c3327966c5ece0dc4dd715ee617d9e30450d8ead95a4' -or $hash -ne $manifest.rcloneSha256) {
    throw 'The bundled rclone does not match the runtime that Mountloom automatically enables.'
}
if ($manifest.PSObject.Properties.Name -contains 'applicationSha256') {
    if ((Get-FileHash -LiteralPath (Join-Path $app 'Mountloom.App.dll') -Algorithm SHA256).Hash -ne $manifest.applicationSha256 -or
        (Get-FileHash -LiteralPath (Join-Path $app 'Mountloom.Core.dll') -Algorithm SHA256).Hash -ne $manifest.coreSha256) {
        throw 'The application files do not match the build manifest.'
    }
}

# The same icon is embedded in the app and shipped for Explorer's mounted drives.
$iconHash = (Get-FileHash -LiteralPath (Join-Path $app 'Mountloom.ico') -Algorithm SHA256).Hash
if (-not $manifest.PSObject.Properties['iconSha256'] -or $iconHash -ne $manifest.iconSha256) {
    throw 'The mounted drive icon does not match the build manifest.'
}

# Probe only the already verified binary; never read the user's rclone settings or contact a remote.
$start = New-Object Diagnostics.ProcessStartInfo
$start.FileName = $rclone
$start.Arguments = 'version'
$start.WorkingDirectory = $app
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
foreach ($key in @($start.EnvironmentVariables.Keys)) {
    if ($key.StartsWith('RCLONE_', [StringComparison]::OrdinalIgnoreCase)) { $start.EnvironmentVariables.Remove($key) }
}
$start.EnvironmentVariables['RCLONE_CONFIG'] = 'NUL'
$process = New-Object Diagnostics.Process
$process.StartInfo = $start
try {
    if (-not $process.Start()) { throw 'The bundled rclone could not be started.' }
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(10000)) {
        $process.Kill()
        $process.WaitForExit()
        throw 'The bundled rclone version check timed out.'
    }
    $version = ($stdout.GetAwaiter().GetResult() -split '[\r\n]')[0]
    $errorOutput = $stderr.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0 -or $version -ne 'rclone v1.75.2') {
        throw "The bundled rclone failed its version check: $errorOutput"
    }
    Write-Host "Package verified: self-contained Windows x64, $version, pinned SHA-256, runtime and license files."
}
finally { $process.Dispose() }
