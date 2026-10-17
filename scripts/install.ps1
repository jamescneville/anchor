# Installs Anchor for the current user: copies dist\Anchor.exe, adds a Start Menu shortcut, starts it with Windows.
param([switch]$NoAutostart)
$ErrorActionPreference = 'Stop'
$src  = Join-Path (Split-Path $PSScriptRoot -Parent) 'dist\Anchor.exe'
if (-not (Test-Path $src)) { throw "Run scripts\publish.ps1 first ($src not found)." }
$dest = Join-Path $env:LOCALAPPDATA 'Programs\Anchor'
Get-Process Anchor -ErrorAction SilentlyContinue | Stop-Process -Force
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item $src "$dest\Anchor.exe" -Force

$lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'Anchor.lnk'
$sh = New-Object -ComObject WScript.Shell
$s = $sh.CreateShortcut($lnk); $s.TargetPath = "$dest\Anchor.exe"; $s.WorkingDirectory = $dest; $s.Save()

if (-not $NoAutostart) {
    Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name Anchor -Value "`"$dest\Anchor.exe`" --background"
}
Start-Process "$dest\Anchor.exe"
Write-Host "Installed to $dest"
