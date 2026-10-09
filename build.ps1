# Copyright (c) 2026 AJACE Holdings LLC. Licensed under the MIT License. See LICENSE in the repository root.

<#
.SYNOPSIS
    Builds DisplayToggle.exe with the C# compiler that ships with Windows (no SDK needed).

.PARAMETER Install
    Also copies the app to %LOCALAPPDATA%\Programs\DisplayToggle and puts a
    "Display Toggle" shortcut on the Desktop.
#>
param([switch]$Install)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$bin = Join-Path $root 'bin'
New-Item -ItemType Directory -Force -Path $bin | Out-Null

$icon = Join-Path $bin 'DisplayToggle.ico'
& (Join-Path $root 'tools\New-AppIcon.ps1') -Path $icon

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }

$exe = Join-Path $bin 'DisplayToggle.exe'
$sources = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }
& $csc /nologo /target:winexe /optimize+ /codepage:65001 "/out:$exe" "/win32icon:$icon" `
    "/win32manifest:$(Join-Path $root 'src\app.manifest')" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll $sources
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Host "Built $exe"

if ($Install) {
    $dest = Join-Path $env:LOCALAPPDATA 'Programs\DisplayToggle'
    $target = Join-Path $dest 'DisplayToggle.exe'
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Get-Process DisplayToggle -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $target } | Stop-Process -Force
    Copy-Item $exe $target -Force

    $shortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Display Toggle.lnk'
    $lnk = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut)
    $lnk.TargetPath = $target
    $lnk.WorkingDirectory = $dest
    $lnk.IconLocation = "$target,0"
    $lnk.Description = 'Turn displays on or off'
    $lnk.Save()
    Write-Host "Installed to $dest"
    Write-Host "Shortcut: $shortcut"
}
