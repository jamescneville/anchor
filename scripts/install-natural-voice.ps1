# Installs an offline neural text-to-speech voice for Anchor's alerts (Piper, MIT licence) into %LOCALAPPDATA%\Anchor\voices.
# Downloads about 86 MB: the Piper program (GitHub), and the en_US "lessac" medium voice (Hugging Face, rhasspy/piper-voices).
# Safe to re-run. Remove the voices folder to uninstall.
$ErrorActionPreference = 'Stop'
$base = Join-Path $env:LOCALAPPDATA 'Anchor\voices'
$piperUrl = 'https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip'
$piperSize = 22477236
$voiceUrl = 'https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/lessac/medium/en_US-lessac-medium.onnx'
$voiceSha = '5efe09e69902187827af646e1a6e9d269dee769f9877d17b16b1b46eeaaf019f'
$jsonUrl = "$voiceUrl.json"

Write-Host "This downloads about 86 MB into $base"
New-Item -ItemType Directory -Force "$base\models" | Out-Null

if (-not (Test-Path "$base\piper\piper\piper.exe")) {
    $tmp = Join-Path $base ('_dl_' + [guid]::NewGuid().ToString('N'))          # each download gets its own new folder
    New-Item -ItemType Directory $tmp | Out-Null
    Invoke-WebRequest $piperUrl -OutFile "$tmp\piper.zip"
    if ((Get-Item "$tmp\piper.zip").Length -ne $piperSize) { throw 'Piper download has an unexpected size; aborting.' }
    New-Item -ItemType Directory -Force "$base\piper" | Out-Null
    Expand-Archive "$tmp\piper.zip" -DestinationPath "$base\piper" -Force
    Remove-Item $tmp -Recurse -Force
}
if (-not (Test-Path "$base\models\en_US-lessac-medium.onnx")) {
    $tmp = Join-Path $base ('_dl_' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory $tmp | Out-Null
    Invoke-WebRequest $voiceUrl -OutFile "$tmp\voice.onnx"
    if ((Get-FileHash "$tmp\voice.onnx" -Algorithm SHA256).Hash.ToLower() -ne $voiceSha) { throw 'Voice file checksum mismatch; aborting.' }
    Invoke-WebRequest $jsonUrl -OutFile "$tmp\voice.onnx.json"
    Move-Item "$tmp\voice.onnx" "$base\models\en_US-lessac-medium.onnx" -Force
    Move-Item "$tmp\voice.onnx.json" "$base\models\en_US-lessac-medium.onnx.json" -Force
    Remove-Item $tmp -Recurse -Force
}
Write-Host 'Done. In Anchor: Settings, Voice, choose "Natural (Piper)", then Hear it.'
