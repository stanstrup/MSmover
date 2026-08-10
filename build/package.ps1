<#
.SYNOPSIS
    Publishes the single-file MSmover executable, stamped with a given version, and writes a
    SHA-256 checksum next to it.

.DESCRIPTION
    Called by semantic-release (via @semantic-release/exec) during a release, and usable by hand
    for a local build:

        powershell -File build\package.ps1 -Version 0.2.0

    Keeping this in a script rather than inline in the workflow means a release build can be
    reproduced locally, byte-for-byte apart from the compiler's own non-determinism.

.PARAMETER Version
    Semver, without a leading "v". Pre-release suffixes such as 0.2.0-rc1 are accepted.

.PARAMETER OutDir
    Where the named executable and its checksum are written. Defaults to .\release.

.PARAMETER SkipInstaller
    Build only the portable executable. Otherwise an NSIS installer is built too, if makensis can
    be found; when it cannot, a warning is printed and the portable build still succeeds.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$OutDir = 'release',
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$') {
    throw "Version '$Version' is not semver. Expected something like 1.2.3 or 1.2.3-rc1."
}

# AssemblyVersion and FileVersion must be four numeric parts, so any pre-release suffix is
# dropped for those and kept only for the informational version.
$numeric = "$(($Version -split '-')[0]).0"

Write-Host "Publishing MSmover $Version (assembly $numeric)" -ForegroundColor Cyan

$publishDir = Join-Path $repo 'publish\win-x64'
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

& dotnet publish (Join-Path $repo 'src\MSmover.App\MSmover.App.csproj') `
    -c Release -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:Version=$Version `
    -p:AssemblyVersion=$numeric `
    -p:FileVersion=$numeric `
    -p:InformationalVersion=$Version `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

$target = Join-Path $repo $OutDir
if (Test-Path $target) { Remove-Item $target -Recurse -Force }
New-Item -ItemType Directory -Force -Path $target | Out-Null

$name = "MSmover-$Version-win-x64.exe"
$exe = Join-Path $target $name
Copy-Item (Join-Path $publishDir 'MSmover.exe') $exe

function Write-Checksum([string]$Path) {
    $fileName = Split-Path -Leaf $Path
    $sha = (Get-FileHash $Path -Algorithm SHA256).Hash.ToLower()
    "$sha  $fileName" | Out-File "$Path.sha256" -Encoding ascii
    return $sha
}

$hash = Write-Checksum $exe

# The version in the file properties has to match what we were asked to build, otherwise the
# download and the release notes would disagree about what this binary is.
$stamped = (Get-Item $exe).VersionInfo.ProductVersion
if ($stamped -notlike "$Version*") {
    throw "Executable reports version '$stamped' but '$Version' was requested."
}

$sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ""
Write-Host "  file    $name"
Write-Host "  size    $sizeMb MB"
Write-Host "  version $stamped"
Write-Host "  sha256  $hash"

# ---- NSIS installer -------------------------------------------------------------------------

if (-not $SkipInstaller) {
    $makensis = $null
    $candidates = @(
        'makensis.exe',
        (Join-Path ${env:ProgramFiles(x86)} 'NSIS\makensis.exe'),
        (Join-Path $env:ProgramFiles 'NSIS\makensis.exe')
    )
    foreach ($candidate in $candidates) {
        if (-not $candidate) { continue }
        $found = Get-Command $candidate -ErrorAction SilentlyContinue
        if ($found) { $makensis = $found.Source; break }
        if (Test-Path $candidate) { $makensis = $candidate; break }
    }

    if (-not $makensis) {
        Write-Warning "makensis was not found, so no installer was built. Install NSIS (https://nsis.sourceforge.io) or pass -SkipInstaller to silence this."
    }
    else {
        $setupName = "MSmover-$Version-win-x64-setup.exe"
        $setup = Join-Path $target $setupName
        Write-Host ""
        Write-Host "Building installer with $makensis" -ForegroundColor Cyan

        & $makensis `
            "/DVERSION=$Version" `
            "/DNUMERIC_VERSION=$numeric" `
            "/DPAYLOAD=$exe" `
            "/DOUTFILE=$setup" `
            "/DROOT=$repo" `
            (Join-Path $PSScriptRoot 'installer.nsi')
        if ($LASTEXITCODE -ne 0) { throw "makensis failed with exit code $LASTEXITCODE." }
        if (-not (Test-Path $setup)) { throw "makensis reported success but $setupName was not produced." }

        $setupHash = Write-Checksum $setup
        $setupMb = [math]::Round((Get-Item $setup).Length / 1MB, 1)
        Write-Host ""
        Write-Host "  file    $setupName"
        Write-Host "  size    $setupMb MB"
        Write-Host "  sha256  $setupHash"
    }
}

Write-Host ""
Write-Host "Wrote $target" -ForegroundColor Green
Get-ChildItem $target | ForEach-Object { Write-Host ("  " + $_.Name) }
