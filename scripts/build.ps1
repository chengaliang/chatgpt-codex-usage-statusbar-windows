$ErrorActionPreference = 'Stop'

$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$sourceDirectory = Join-Path $root 'src'
$distributionDirectory = Join-Path $root 'dist'
$outputPath = Join-Path $distributionDirectory 'SubscriptionStatus.exe'
$manifestPath = Join-Path $distributionDirectory 'SHA256SUMS.txt'
$iconPath = Join-Path $root 'assets\ChatGPTCodexUsageStatusBar.ico'
$legacySourcePath = Join-Path $PSScriptRoot 'LegacyLauncher.cs'
$legacyOutputPath = Join-Path $root 'SubscriptionStatus.exe'

New-Item -ItemType Directory -Force -Path $distributionDirectory | Out-Null

$systemRoot = $env:SystemRoot
if ([string]::IsNullOrWhiteSpace($systemRoot)) {
    $systemRoot = $env:WINDIR
}
if ([string]::IsNullOrWhiteSpace($systemRoot)) {
    throw 'SystemRoot/WINDIR is not set; cannot locate the .NET Framework compiler.'
}

$compilerCandidates = @(
    (Join-Path $systemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $systemRoot 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compiler) {
    throw 'csc.exe was not found. Install the .NET Framework developer tools first.'
}

$sources = @(
    Get-ChildItem -LiteralPath $sourceDirectory -File -Filter '*.cs' |
        Sort-Object Name |
        Select-Object -ExpandProperty FullName
)
if ($sources.Count -eq 0) {
    throw "No C# source files found in $sourceDirectory."
}
if (-not (Test-Path -LiteralPath $iconPath -PathType Leaf)) {
    throw "Application icon is missing: $iconPath. Run scripts\generate-icon.ps1 first."
}

Remove-Item -LiteralPath $outputPath -Force -ErrorAction SilentlyContinue

$compilerArguments = @(
    '/nologo'
    '/target:winexe'
    '/platform:anycpu'
    '/optimize+'
    '/warn:4'
    '/warnaserror+'
    '/reference:System.dll'
    '/reference:System.Core.dll'
    '/reference:System.Drawing.dll'
    '/reference:System.Net.Http.dll'
    '/reference:System.Windows.Forms.dll'
    '/reference:System.Web.Extensions.dll'
    '/utf8output'
    ('/win32icon:' + $iconPath)
    ('/out:' + $outputPath)
)
$compilerArguments += $sources

& $compiler @compilerArguments
if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path -LiteralPath $legacySourcePath -PathType Leaf)) {
    throw "Compatibility launcher source is missing: $legacySourcePath"
}

$legacyArguments = @(
    '/nologo'
    '/target:winexe'
    '/platform:anycpu'
    '/optimize+'
    '/warn:4'
    '/warnaserror+'
    '/reference:System.dll'
    '/reference:System.Windows.Forms.dll'
    '/utf8output'
    ('/win32icon:' + $iconPath)
    ('/out:' + $legacyOutputPath)
    $legacySourcePath
)
& $compiler @legacyArguments
if ($LASTEXITCODE -ne 0) {
    throw "Compatibility launcher compilation failed with exit code $LASTEXITCODE."
}

$hash = (Get-FileHash -LiteralPath $outputPath -Algorithm SHA256).Hash.ToUpperInvariant()
[System.IO.File]::WriteAllText(
    $manifestPath,
    ($hash + "  SubscriptionStatus.exe" + [Environment]::NewLine),
    [System.Text.Encoding]::ASCII
)

Write-Output "Built $outputPath"
Write-Output "SHA256 $hash"
