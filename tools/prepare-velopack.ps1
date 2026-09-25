param(
    [string]$CacheRoot = (Join-Path $PSScriptRoot '.cache')
)

$ErrorActionPreference = 'Stop'

# Keep the fork and the official 1.2.0 vendor binaries together inside this project.
# Only the small setup script is tracked; the local cache survives subsequent builds.
$forkUrl = 'https://github.com/Hon-Lu/velopack'
$forkBranch = 'fork/no-stub-1.2.0'
$forkCommit = '823dd6b7d62444ee20e083bca0ab138862d53a48'
$officialVersion = '1.2.0'

$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$cacheFullPath = [System.IO.Path]::GetFullPath($CacheRoot)
if (-not $cacheFullPath.StartsWith($projectRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Build tool cache must be inside the project: $cacheFullPath"
}

$forkRoot = Join-Path $cacheFullPath 'velopack-fork'
$toolRoot = Join-Path $cacheFullPath 'vpk-tool'
$vpkPath = Join-Path $forkRoot 'build\Release\net10.0\vpk.exe'
$forkVendor = Join-Path $forkRoot 'vendor'
$officialVendor = Join-Path $toolRoot '.store\vpk\1.2.0\vpk\1.2.0\vendor'

if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'Git is required.' }

function Assert-ForkCommit {
    if (-not (Test-Path -LiteralPath (Join-Path $forkRoot '.git'))) {
        throw "Incomplete Velopack fork cache: $forkRoot"
    }
    $head = (& git -C $forkRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $head -ne $forkCommit) {
        throw "Velopack fork commit mismatch: expected $forkCommit, got $head"
    }
}

if (Test-Path -LiteralPath $vpkPath) {
    Assert-ForkCommit
    if (-not (Test-Path -LiteralPath (Join-Path $forkVendor 'update.exe'))) {
        throw "Missing official Velopack vendor files in $forkVendor"
    }
    if (Test-Path -LiteralPath (Join-Path $officialVendor 'update.exe')) {
        $forkHash = (Get-FileHash -LiteralPath (Join-Path $forkVendor 'update.exe') -Algorithm SHA256).Hash
        $officialHash = (Get-FileHash -LiteralPath (Join-Path $officialVendor 'update.exe') -Algorithm SHA256).Hash
        if ($forkHash -ne $officialHash) { throw 'Cached Velopack vendor differs from official vpk 1.2.0.' }
    }
    Write-Host "Using project-local Velopack fork: $vpkPath"
    return
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'dotnet SDK is required.' }
New-Item -ItemType Directory -Force -Path $cacheFullPath | Out-Null

if (-not (Test-Path -LiteralPath (Join-Path $officialVendor 'update.exe'))) {
    if (Test-Path -LiteralPath $toolRoot) {
        throw "Incomplete official vpk tool cache: $toolRoot"
    }
    & dotnet tool install vpk --version $officialVersion --tool-path $toolRoot
    if ($LASTEXITCODE -ne 0) { throw 'Could not install official vpk 1.2.0 vendor files.' }
}

if (-not (Test-Path -LiteralPath (Join-Path $forkRoot '.git'))) {
    if (Test-Path -LiteralPath $forkRoot) { throw "Incomplete Velopack fork cache: $forkRoot" }
    # The fork's Git versioning needs branch history; do not use a shallow clone.
    & git -c core.longpaths=true clone --single-branch -b $forkBranch $forkUrl $forkRoot
    if ($LASTEXITCODE -ne 0) { throw 'Could not clone the Velopack fork.' }
}

$head = (& git -C $forkRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Could not read the Velopack fork commit.' }
if ($head -ne $forkCommit) {
    & git -C $forkRoot checkout --detach $forkCommit
    if ($LASTEXITCODE -ne 0) { throw "Could not check out pinned fork commit $forkCommit." }
}
Assert-ForkCommit

New-Item -ItemType Directory -Force -Path $forkVendor | Out-Null
Copy-Item -Path (Join-Path $officialVendor '*') -Destination $forkVendor -Recurse -Force
if (-not (Test-Path -LiteralPath (Join-Path $forkVendor 'update.exe'))) {
    throw 'The official Velopack vendor files were not copied.'
}

& dotnet build (Join-Path $forkRoot 'src\vpk\Velopack.Vpk') -c Release -f net10.0
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $vpkPath)) {
    throw 'Could not build the Velopack fork.'
}
Write-Host "Built project-local Velopack fork: $vpkPath"
