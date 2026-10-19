# Builds dist\Anchor-Setup-<version>.exe: publishes the app, then compiles installer\Anchor.iss with Inno Setup 6.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 is required (https://jrsoftware.org/isdl.php or: winget install JRSoftware.InnoSetup)." }
$version = ([xml](Get-Content "$root\src\Anchor.Desktop\Anchor.Desktop.csproj")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
& "$PSScriptRoot\publish.ps1"
& $iscc "/DAppVersion=$version" "$root\installer\Anchor.iss"
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }
Write-Host "Built $root\dist\Anchor-Setup-$version.exe"
