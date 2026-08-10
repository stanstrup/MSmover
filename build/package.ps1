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

<#
    Authenticode signing, if a certificate has been supplied.

    Set MSMOVER_SIGN_PFX_BASE64 (a base64-encoded .pfx) and MSMOVER_SIGN_PASSWORD to enable it;
    with neither set, the build produces unsigned binaries and says so. That keeps the pipeline
    working for anyone building from source while letting a release be signed by adding two
    repository secrets and nothing else.

    Signing has to happen before the checksum is written, and the payload has to be signed before
    the installer is built, so that the file which ends up on disk after installation is signed
    too, not just the installer that put it there.
#>
$script:SignTool = $null
$script:PfxPath = $null

function Initialize-Signing {
    if (-not $env:MSMOVER_SIGN_PFX_BASE64) {
        Write-Warning "No signing certificate supplied (MSMOVER_SIGN_PFX_BASE64 is not set); binaries will be unsigned."
        return $false
    }

    $found = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } |
        Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $found) {
        throw "A signing certificate was supplied but signtool.exe could not be found. Install the Windows SDK."
    }

    $script:SignTool = $found.FullName
    $script:PfxPath = Join-Path ([IO.Path]::GetTempPath()) "msmover-signing-$([guid]::NewGuid()).pfx"
    [IO.File]::WriteAllBytes($script:PfxPath, [Convert]::FromBase64String($env:MSMOVER_SIGN_PFX_BASE64))
    Write-Host "Signing with $($script:SignTool)" -ForegroundColor Cyan
    return $true
}

function Invoke-Sign([string]$Path) {
    if (-not $script:SignTool) { return }

    # RFC 3161 timestamping, so signatures stay valid after the certificate expires.
    & $script:SignTool sign `
        /f $script:PfxPath `
        /p $env:MSMOVER_SIGN_PASSWORD `
        /fd SHA256 `
        /tr http://timestamp.digicert.com `
        /td SHA256 `
        /d "MSmover" `
        /du "https://github.com/stanstrup/MSmover" `
        $Path
    if ($LASTEXITCODE -ne 0) { throw "signtool failed on $Path with exit code $LASTEXITCODE." }
}

$signing = Initialize-Signing
try {

Invoke-Sign $exe
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
        $nsi = Join-Path $PSScriptRoot 'installer.nsi'

        # makensis reads a script without a byte-order mark in the machine's ANSI code page, so a
        # single non-ASCII character can build here and fail there. Catch it deterministically
        # rather than discovering it on someone else's machine.
        $offending = [IO.File]::ReadAllText($nsi).ToCharArray() | Where-Object { [int]$_ -gt 127 } | Select-Object -Unique
        if ($offending) {
            $shown = ($offending | ForEach-Object { "U+{0:X4}" -f [int]$_ }) -join ', '
            throw "installer.nsi must be pure ASCII but contains $shown. Replace those characters; " +
                  "makensis reads a BOM-less script in the local ANSI code page."
        }

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
            $nsi
        if ($LASTEXITCODE -ne 0) { throw "makensis failed with exit code $LASTEXITCODE." }
        if (-not (Test-Path $setup)) { throw "makensis reported success but $setupName was not produced." }

        Invoke-Sign $setup
        $setupHash = Write-Checksum $setup
        $setupMb = [math]::Round((Get-Item $setup).Length / 1MB, 1)
        Write-Host ""
        Write-Host "  file    $setupName"
        Write-Host "  size    $setupMb MB"
        Write-Host "  sha256  $setupHash"
    }
}

Write-Host ""
if ($signing) {
    Write-Host "Binaries are Authenticode signed." -ForegroundColor Green
} else {
    Write-Host "Binaries are UNSIGNED. Windows SmartScreen will warn about an unknown publisher." -ForegroundColor Yellow
}
Write-Host "Wrote $target" -ForegroundColor Green
Get-ChildItem $target | ForEach-Object { Write-Host ("  " + $_.Name) }

}
finally {
    if ($script:PfxPath -and (Test-Path $script:PfxPath)) {
        Remove-Item $script:PfxPath -Force -ErrorAction SilentlyContinue
    }
}
