# Builds a self-contained single-file Anchor.exe into ./dist (no .NET install needed on the target PC).
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
dotnet publish "$root\src\Anchor.Desktop" -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$root\dist"
Write-Host "Built $root\dist\Anchor.exe"
