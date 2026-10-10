# Copyright (c) 2026 AJACE Holdings LLC. Licensed under the MIT License. See LICENSE in the repository root.

<#
.SYNOPSIS
    Builds Display Toggle with the C# compiler that ships with Windows (no SDK needed).

    bin\DisplayToggle.exe        the app (runs from anywhere, no install needed)
    bin\DisplayToggle-Setup.exe  installer with the app embedded inside it

.PARAMETER Install
    Also installs it on this PC by running the installer silently
    (%LOCALAPPDATA%\Programs\DisplayToggle, desktop + Start menu icons, Installed apps entry).

.PARAMETER Package
    Also copies the release files and a SHA256SUMS.txt into dist\, ready to attach to a GitHub release.
#>
param([switch]$Install, [switch]$Package)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$bin = Join-Path $root 'bin'
New-Item -ItemType Directory -Force -Path $bin | Out-Null

$icon = Join-Path $bin 'DisplayToggle.ico'
& (Join-Path $root 'tools\New-AppIcon.ps1') -Path $icon

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }

# The app
$exe = Join-Path $bin 'DisplayToggle.exe'
$sources = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }
& $csc /nologo /target:winexe /optimize+ /codepage:65001 "/out:$exe" "/win32icon:$icon" `
    "/win32manifest:$(Join-Path $root 'src\app.manifest')" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll $sources
if ($LASTEXITCODE -ne 0) { throw 'Build failed (app).' }
Write-Host "Built $exe"

# The installer: shares AssemblyInfo.cs with the app (same version and company) and carries the app as a resource.
$setup = Join-Path $bin 'DisplayToggle-Setup.exe'
& $csc /nologo /target:winexe /optimize+ /codepage:65001 "/out:$setup" "/win32icon:$icon" `
    "/win32manifest:$(Join-Path $root 'installer\setup.manifest')" "/resource:$exe,DisplayToggle.exe" `
    /r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:Microsoft.CSharp.dll `
    (Join-Path $root 'installer\Setup.cs') (Join-Path $root 'src\AssemblyInfo.cs')
if ($LASTEXITCODE -ne 0) { throw 'Build failed (installer).' }
Write-Host "Built $setup"

if ($Package) {
    $dist = Join-Path $root 'dist'
    if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
    New-Item -ItemType Directory -Path $dist | Out-Null
    Copy-Item $setup, $exe $dist
    $sums = Get-ChildItem $dist -Filter *.exe | Sort-Object Name | ForEach-Object {
        (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower() + '  ' + $_.Name
    }
    Set-Content (Join-Path $dist 'SHA256SUMS.txt') $sums -Encoding ascii
    Write-Host "Release files in $dist"
    $sums | ForEach-Object { Write-Host "  $_" }
}

if ($Install) {
    $p = Start-Process $setup -ArgumentList '/S' -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "Installer failed (exit code $($p.ExitCode))." }
    Write-Host "Installed to $(Join-Path $env:LOCALAPPDATA 'Programs\DisplayToggle') with desktop and Start menu icons."
}
