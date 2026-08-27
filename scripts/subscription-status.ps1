# Compatibility entry point for launching the status bar executable.
$ErrorActionPreference = 'Stop'

$exePath = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..\dist\SubscriptionStatus.exe')
)
if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
    throw "SubscriptionStatus.exe not found in dist: $exePath"
}

$workingDirectory = Split-Path -Parent $exePath
Start-Process -FilePath $exePath -WorkingDirectory $workingDirectory
