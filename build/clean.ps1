<#
.SYNOPSIS
    Removes build output from the repository.

.DESCRIPTION
    A self-contained publish is around 70 MB, and between bin, obj, publish and release a working
    tree accumulates several copies of the application. This removes them.

    Only known build directories are touched. Nothing under %APPDATA%\MSmover is affected, so your
    rules, logs and transfer journal are safe.

.PARAMETER WhatIf
    Report what would be removed without removing anything.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param()

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

$targets = @()
$targets += Get-ChildItem $repo -Recurse -Directory -Include 'bin', 'obj' -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notlike '*\node_modules\*' }
foreach ($name in 'publish', 'release', 'docs\_site') {
    $path = Join-Path $repo $name
    if (Test-Path $path) { $targets += Get-Item $path }
}

if (-not $targets) {
    Write-Host "Nothing to clean." -ForegroundColor Green
    return
}

# A running instance holds its own executable open, which would make the removal fail halfway.
$running = Get-Process | Where-Object { $_.ProcessName -like 'MSmover*' }
if ($running) {
    Write-Warning ("MSmover is running from: " +
        (($running | ForEach-Object { $_.Path }) -join ', ') +
        ". Close it first, or its folder cannot be removed.")
}

$total = 0
foreach ($t in $targets) {
    $size = (Get-ChildItem $t.FullName -Recurse -File -ErrorAction SilentlyContinue |
             Measure-Object Length -Sum).Sum
    $total += $size
    $label = "{0,8:N1} MB  {1}" -f ($size / 1MB), $t.FullName.Substring($repo.Length + 1)
    if ($PSCmdlet.ShouldProcess($t.FullName, 'Remove')) {
        Remove-Item -LiteralPath $t.FullName -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "removed  $label"
    }
    else {
        Write-Host "would remove  $label"
    }
}

Write-Host ""
Write-Host ("{0:N0} MB accounted for." -f ($total / 1MB)) -ForegroundColor Green
Write-Host "Generated API metadata under docs\api is left alone; docfx regenerates it."
